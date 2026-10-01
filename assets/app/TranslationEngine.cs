using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SwipeTranslate
{
    // The default provider sends selected text only to a loopback Ollama server.
    // Online providers are opt-in; an error never switches providers. The cache
    // is bounded, held in memory, and cleared when the application closes.
    public sealed class TranslationEngine : IDisposable
    {
        private const int MaximumTextLength = 4000;
        private const int MaximumRequestBytes = 500;
        private const int MaximumCacheEntries = 128;
        public const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";
        public const string DefaultOllamaModel = "swipetranslate-hymt2";
        private const int OllamaTimeoutSeconds = 45;
        private readonly HttpClient client;
        private readonly Uri ollamaChatAddress;
        private readonly object cacheLock = new object();
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private readonly Queue<string> cacheOrder = new Queue<string>();
        private volatile bool disposed;

        public string Provider { get; private set; }
        public string OllamaEndpoint { get; private set; }
        public string OllamaModel { get; private set; }
        // Diagnostics contain counts and timings only, never selected content.
        public long LastDurationMs { get; private set; }
        public bool LastCacheHit { get; private set; }
        public int LastInputCharacters { get; private set; }
        public int LastOutputCharacters { get; private set; }
        public long LastPromptEvalCount { get; private set; }
        public long LastEvalCount { get; private set; }
        public long LastEvalDurationNs { get; private set; }

        public TranslationEngine() : this("ollama") { }

        public TranslationEngine(string provider)
            : this(provider, DefaultOllamaEndpoint, DefaultOllamaModel) { }

        public TranslationEngine(string provider, string ollamaEndpoint, string ollamaModel)
        {
            Provider = String.IsNullOrWhiteSpace(provider) ? "ollama" : provider.Trim().ToLowerInvariant();
            if (Provider != "ollama" && Provider != "google" && Provider != "mymemory")
                throw new ArgumentException("不支持这个翻译服务，请选择 ollama、google 或 mymemory。", "provider");
            if (Provider == "ollama")
            {
                Uri endpoint;
                if (!Uri.TryCreate(ollamaEndpoint, UriKind.Absolute, out endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
                    !endpoint.IsLoopback || !String.IsNullOrEmpty(endpoint.UserInfo) ||
                    !String.IsNullOrEmpty(endpoint.Query) || !String.IsNullOrEmpty(endpoint.Fragment))
                    throw new ArgumentException("本地翻译地址必须是本机的 HTTP 地址。", "ollamaEndpoint");
                if (String.IsNullOrWhiteSpace(ollamaModel))
                    throw new ArgumentException("请设置本地翻译模型名称。", "ollamaModel");
                OllamaEndpoint = endpoint.AbsoluteUri.TrimEnd('/');
                OllamaModel = ollamaModel.Trim();
                ollamaChatAddress = new Uri(OllamaEndpoint + "/api/chat");
            }
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            HttpClientHandler handler = new HttpClientHandler();
            if (Provider == "ollama")
            {
                // A machine-wide proxy or HTTP redirect must not move local text
                // off the device, even if such settings change while we run.
                handler.UseProxy = false;
                handler.AllowAutoRedirect = false;
            }
            client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(Provider == "ollama" ? OllamaTimeoutSeconds : 12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BabelTower/0.1");
        }

        public async Task<string> TranslateAsync(string text, string sourceLanguage,
            string targetLanguage, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException("TranslationEngine");
            token.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(text)) return text ?? String.Empty;
            if (text.Length > MaximumTextLength)
                throw new InvalidOperationException("选中的文字太长，请每次选择不超过 4000 个字符。");
            Stopwatch duration = Stopwatch.StartNew();
            LastDurationMs = 0;
            LastCacheHit = false;
            LastInputCharacters = text.Length;
            LastOutputCharacters = 0;
            LastPromptEvalCount = 0;
            LastEvalCount = 0;
            LastEvalDurationNs = 0;

            string source = NormalizeLanguage(sourceLanguage, true);
            string target = NormalizeLanguage(targetLanguage, false);
            if (source == "auto" && Provider == "mymemory") source = DetectLanguage(text);
            if (String.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                LastOutputCharacters = text.Length;
                LastDurationMs = duration.ElapsedMilliseconds;
                return text;
            }

            string cacheKey = Provider + "\u0001" + source + "\u0001" + target + "\u0001" + text;
            string cached;
            lock (cacheLock)
            {
                if (cache.TryGetValue(cacheKey, out cached))
                {
                    LastCacheHit = true;
                    LastOutputCharacters = cached.Length;
                    LastDurationMs = duration.ElapsedMilliseconds;
                    return cached;
                }
            }

            string translated;
            if (Provider == "ollama")
            {
                // The local model needs the complete selection for context. The
                // 500-byte limit applies only to the legacy online interfaces.
                translated = await TranslateOllamaAsync(text, target, token).ConfigureAwait(false);
            }
            else
            {
                StringBuilder result = new StringBuilder();
                foreach (TextPiece piece in SplitText(text))
                {
                    token.ThrowIfCancellationRequested();
                    if (!piece.Translate)
                    {
                        result.Append(piece.Text);
                        continue;
                    }
                    result.Append(await TranslatePieceAsync(piece.Text, source, target, token)
                        .ConfigureAwait(false));
                }
                translated = result.ToString();
            }

            token.ThrowIfCancellationRequested();
            LastOutputCharacters = translated.Length;
            LastDurationMs = duration.ElapsedMilliseconds;
            lock (cacheLock)
            {
                if (!disposed && !cache.ContainsKey(cacheKey))
                {
                    while (cache.Count >= MaximumCacheEntries)
                        cache.Remove(cacheOrder.Dequeue());
                    cache.Add(cacheKey, translated);
                    cacheOrder.Enqueue(cacheKey);
                }
            }
            return translated;
        }

        private async Task<string> TranslateOllamaAsync(string text, string target,
            CancellationToken token)
        {
            string targetName = GetTargetLanguageName(target);
            // Official Hy-MT2 prompt and sampling settings; no generic system
            // prompt is added, because this model was tuned for translation.
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["model"] = OllamaModel;
            request["stream"] = false;
            request["keep_alive"] = "30m";
            request["messages"] = new object[] {
                new Dictionary<string, object> {
                    { "role", "user" },
                    { "content", "将以下文本翻译为" + targetName +
                        "，注意只需要输出翻译后的结果，不要额外解释：\n" + text }
                }
            };
            request["options"] = new Dictionary<string, object> {
                { "num_ctx", 8192 }, { "num_predict", 2048 },
                { "temperature", 0.7 }, { "top_p", 0.6 }, { "top_k", 20 },
                { "repeat_penalty", 1.05 }
            };
            string json = new JavaScriptSerializer().Serialize(request);
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(OllamaTimeoutSeconds));
                try
                {
                    using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (HttpResponseMessage response = await client.PostAsync(ollamaChatAddress,
                        content, timeout.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        timeout.Token.ThrowIfCancellationRequested();
                        if ((int)response.StatusCode == 404)
                            throw new InvalidOperationException("本地翻译模型尚未就绪，请先完成腾讯 Hy-MT2 模型安装，再运行本地版启动程序。");
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("本地翻译服务暂时不可用，请重新启动巴别塔后重试。");

                        Dictionary<string, object> data = null;
                        try
                        {
                            JavaScriptSerializer serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = 1048576;
                            data = serializer.Deserialize<Dictionary<string, object>>(body);
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                        object value;
                        Dictionary<string, object> message = null;
                        if (data != null && data.TryGetValue("message", out value))
                            message = value as Dictionary<string, object>;
                        if (data != null && data.TryGetValue("done_reason", out value) &&
                            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "length",
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("这段文字的译文太长，请缩小选区后重试。");
                        string translated = message != null && message.TryGetValue("content", out value)
                            ? value as string : null;
                        // Never read or display a separate thinking field. Strip
                        // explicit thinking tags if a server template inserts them.
                        if (translated != null)
                            translated = Regex.Replace(translated, "<think>.*?</think>", String.Empty,
                                RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
                        if (String.IsNullOrWhiteSpace(translated) ||
                            translated.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new InvalidOperationException("本地模型没有返回有效译文，请缩小选区后重试。");
                        LastPromptEvalCount = ReadCounter(data, "prompt_eval_count");
                        LastEvalCount = ReadCounter(data, "eval_count");
                        LastEvalDurationNs = ReadCounter(data, "eval_duration");
                        return translated;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new TimeoutException("本地翻译超过 45 秒未响应，请确认模型已启动或缩小选区。");
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("无法连接本机 Ollama 翻译服务，请启动 Ollama 或运行本地版启动程序后重试。");
                }
            }
        }

        private static long ReadCounter(Dictionary<string, object> data, string name)
        {
            object value;
            long result;
            if (data != null && data.TryGetValue(name, out value) &&
                Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result))
                return result;
            return 0;
        }

        private static string GetTargetLanguageName(string target)
        {
            switch (target.ToLowerInvariant())
            {
                case "zh-cn": return "简体中文";
                case "zh-tw":
                case "zh-hant": return "繁体中文";
                case "en": return "英语";
                case "ja": return "日语";
                case "ko": return "韩语";
                case "fr": return "法语";
                case "de": return "德语";
                case "es": return "西班牙语";
                case "ru": return "俄语";
                case "pt": return "葡萄牙语";
                case "it": return "意大利语";
                case "ar": return "阿拉伯语";
                case "th": return "泰语";
                case "vi": return "越南语";
                case "id": return "印度尼西亚语";
                case "tr": return "土耳其语";
                case "pl": return "波兰语";
                case "nl": return "荷兰语";
                case "cs": return "捷克语";
                case "uk": return "乌克兰语";
                case "hi": return "印地语";
                default: throw new InvalidOperationException("巴别塔暂不支持这个目标语言，请在设置中重新选择。");
            }
        }

        private async Task<string> TranslatePieceAsync(string text, string source,
            string target, CancellationToken token)
        {
            if (Provider == "google")
                return await TranslateGooglePieceAsync(text, source, target, token).ConfigureAwait(false);
            return await TranslateMyMemoryPieceAsync(text, source, target, token).ConfigureAwait(false);
        }

        // Google's public web translation interface is suitable for a prototype.
        // It is not the supported Google Cloud Translation API and has no service
        // guarantee. We do not silently send text to a different provider on error.
        private async Task<string> TranslateGooglePieceAsync(string text, string source,
            string target, CancellationToken token)
        {
            string address = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=" +
                Uri.EscapeDataString(source) + "&tl=" + Uri.EscapeDataString(target) +
                "&dt=t&q=" + Uri.EscapeDataString(text);
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    using (HttpResponseMessage response = await client.GetAsync(address,
                        HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        timeout.Token.ThrowIfCancellationRequested();
                        if ((int)response.StatusCode == 429)
                            throw new InvalidOperationException("在线翻译请求过于频繁，请稍后再试。");
                        if ((int)response.StatusCode == 400)
                            throw new InvalidOperationException("翻译服务无法处理这段文字或语言设置，请更换源语言后重试。");
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("无法连接在线翻译服务，请检查网络后重试。");

                        object[] root = null;
                        try
                        {
                            JavaScriptSerializer serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = 1048576;
                            root = serializer.DeserializeObject(body) as object[];
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }

                        object[] segments = root != null && root.Length > 0 ? root[0] as object[] : null;
                        StringBuilder translated = new StringBuilder();
                        if (segments != null)
                        {
                            foreach (object value in segments)
                            {
                                object[] segment = value as object[];
                                if (segment != null && segment.Length > 0 && segment[0] is string)
                                    translated.Append((string)segment[0]);
                            }
                        }
                        if (String.IsNullOrWhiteSpace(translated.ToString()))
                            throw new InvalidOperationException("翻译服务没有返回有效译文，请稍后重试。");
                        return WebUtility.HtmlDecode(translated.ToString()).Trim();
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new TimeoutException("在线翻译超过 12 秒未响应，请稍后重试。");
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("无法连接在线翻译服务，请检查网络后重试。");
                }
            }
        }

        private async Task<string> TranslateMyMemoryPieceAsync(string text, string source,
            string target, CancellationToken token)
        {
            string address = "https://api.mymemory.translated.net/get?q=" +
                Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(source + "|" + target);
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    using (HttpResponseMessage response = await client.GetAsync(address,
                        HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        timeout.Token.ThrowIfCancellationRequested();
                        if ((int)response.StatusCode == 429)
                            throw QuotaException();

                        Dictionary<string, object> data = null;
                        try
                        {
                            JavaScriptSerializer serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = 1048576;
                            data = serializer.Deserialize<Dictionary<string, object>>(body);
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }

                        string translated = null;
                        if (data != null)
                        {
                            object value;
                            Dictionary<string, object> responseData = null;
                            if (data.TryGetValue("responseData", out value))
                                responseData = value as Dictionary<string, object>;
                            if (responseData != null && responseData.TryGetValue("translatedText", out value))
                                translated = Convert.ToString(value, CultureInfo.InvariantCulture);

                            int status = 0;
                            if (data.TryGetValue("responseStatus", out value))
                                Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out status);
                            bool quotaFinished = data.TryGetValue("quotaFinished", out value) &&
                                String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "true",
                                    StringComparison.OrdinalIgnoreCase);
                            string details = data.TryGetValue("responseDetails", out value)
                                ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Empty;
                            if (status == 429 || quotaFinished || IsQuotaWarning(details) || IsQuotaWarning(translated))
                                throw QuotaException();
                            if (status != 200)
                            {
                                if (status == 400)
                                    throw new InvalidOperationException("翻译服务无法处理这段文字或语言设置，请缩小选区或更换源语言。");
                                throw new InvalidOperationException("在线翻译服务暂时不可用，请稍后重试。");
                            }
                        }

                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("无法连接在线翻译服务，请检查网络后重试。");
                        if (data == null || String.IsNullOrWhiteSpace(translated))
                            throw new InvalidOperationException("翻译服务没有返回有效译文，请稍后重试。");

                        return WebUtility.HtmlDecode(translated).Trim();
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new TimeoutException("在线翻译超过 12 秒未响应，请稍后重试。");
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("无法连接在线翻译服务，请检查网络后重试。");
                }
            }
        }

        private static bool IsQuotaWarning(string value)
        {
            return value != null && value.IndexOf("USED ALL AVAILABLE FREE TRANSLATIONS",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static InvalidOperationException QuotaException()
        {
            return new InvalidOperationException("在线翻译服务的今日免费额度已用完，暂时无法翻译，请稍后再试。");
        }

        private static string NormalizeLanguage(string language, bool allowAuto)
        {
            if (String.IsNullOrWhiteSpace(language)) return allowAuto ? "auto" : "zh-CN";
            language = language.Trim();
            if (String.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
                return allowAuto ? "auto" : "zh-CN";
            if (String.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(language, "zh-Hans", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (!Regex.IsMatch(language, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$"))
                throw new InvalidOperationException("语言设置无效，请重新选择源语言和目标语言。");
            return language;
        }

        private static string DetectLanguage(string text)
        {
            bool hangul = false, cyrillic = false, han = false;
            for (int index = 0; index < text.Length; index++)
            {
                int code = text[index];
                if (Char.IsHighSurrogate(text[index]) && index + 1 < text.Length &&
                    Char.IsLowSurrogate(text[index + 1])) code = Char.ConvertToUtf32(text, index++);
                if ((code >= 0x3040 && code <= 0x30ff) || (code >= 0xff66 && code <= 0xff9d)) return "ja";
                if ((code >= 0xac00 && code <= 0xd7af) || (code >= 0x1100 && code <= 0x11ff) ||
                    (code >= 0x3130 && code <= 0x318f)) hangul = true;
                if ((code >= 0x0400 && code <= 0x052f) || (code >= 0x2de0 && code <= 0x2dff) ||
                    (code >= 0xa640 && code <= 0xa69f)) cyrillic = true;
                if ((code >= 0x3400 && code <= 0x9fff) || (code >= 0xf900 && code <= 0xfaff) ||
                    (code >= 0x20000 && code <= 0x2ebef)) han = true;
            }
            if (hangul) return "ko";
            if (cyrillic) return "ru";
            if (han) return "zh-CN";
            return "en";
        }

        private sealed class TextPiece
        {
            public readonly string Text;
            public readonly bool Translate;
            public TextPiece(string text, bool translate) { Text = text; Translate = translate; }
        }

        private static IEnumerable<TextPiece> SplitText(string text)
        {
            // Preserve line breaks and whitespace between chunks independently of
            // the translation service, which may normalize them in its response.
            int position = 0;
            while (position < text.Length)
            {
                if (Char.IsWhiteSpace(text[position]))
                {
                    int whitespaceEnd = position + 1;
                    while (whitespaceEnd < text.Length && Char.IsWhiteSpace(text[whitespaceEnd])) whitespaceEnd++;
                    yield return new TextPiece(text.Substring(position, whitespaceEnd - position), false);
                    position = whitespaceEnd;
                    continue;
                }

                int scan = position, bytes = 0, lastBoundary = -1;
                while (scan < text.Length && text[scan] != '\r' && text[scan] != '\n')
                {
                    int width = Char.IsHighSurrogate(text[scan]) && scan + 1 < text.Length &&
                        Char.IsLowSurrogate(text[scan + 1]) ? 2 : 1;
                    int characterBytes = Encoding.UTF8.GetByteCount(text.Substring(scan, width));
                    if (bytes + characterBytes > MaximumRequestBytes) break;
                    bytes += characterBytes;
                    scan += width;
                    if (Char.IsWhiteSpace(text[scan - 1]) || IsSentenceEnd(text[scan - 1])) lastBoundary = scan;
                }

                int end = scan;
                if (scan < text.Length && text[scan] != '\r' && text[scan] != '\n' && lastBoundary > position)
                    end = lastBoundary;
                while (end > position && Char.IsWhiteSpace(text[end - 1])) end--;
                // A Unicode scalar is at most four UTF-8 bytes, so at least one
                // scalar always fits, and a surrogate pair is never divided.
                yield return new TextPiece(text.Substring(position, end - position), true);
                position = end;
            }
        }

        private static bool IsSentenceEnd(char character)
        {
            return character == '.' || character == '!' || character == '?' || character == ';' ||
                character == '\u3002' || character == '\uff01' || character == '\uff1f' || character == '\uff1b';
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            client.Dispose();
            lock (cacheLock)
            {
                cache.Clear();
                cacheOrder.Clear();
            }
        }
    }
}
