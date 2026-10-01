using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SwipeTranslate
{
    // A per-install secret protects this fixed loopback service. A normal web
    // page cannot call it; only authenticated extension or local requests work.
    sealed class BrowserTranslationBridge : IDisposable
    {
        public const string Endpoint = "http://127.0.0.1:17863";
        readonly HttpListener listener = new HttpListener();
        readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        readonly object lifecycle = new object();
        CancellationTokenSource session;
        int generation;
        readonly Func<Task<Options>> readOptions;
        readonly Func<Options, Task> applyOptions;
        readonly Func<string, string, string, CancellationToken, Task<string>> translate;
        readonly string token;
        int requests;
        bool disposed;
        public string PairingCode { get { return token; } }
        public bool Running { get { return !disposed && listener.IsListening; } }
        public string Failure { get; private set; }

        public BrowserTranslationBridge(Func<Task<Options>> readOptions, Func<Options, Task> applyOptions,
            Func<string, string, string, CancellationToken, Task<string>> translate)
            : this(readOptions, applyOptions, translate,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-bridge.json")) { }

        internal BrowserTranslationBridge(Func<Task<Options>> readOptions, Func<Options, Task> applyOptions,
            Func<string, string, string, CancellationToken, Task<string>> translate, string pairingFile)
        {
            this.readOptions = readOptions; this.applyOptions = applyOptions; this.translate = translate;
            token = LoadToken(pairingFile);
            listener.Prefixes.Add(Endpoint + "/");
        }

        static string LoadToken(string path)
        {
            string saved = null;
            if (File.Exists(path))
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (data != null) data.TryGetValue("token", out saved);
                if (!Regex.IsMatch(saved ?? "", "\\A[a-f0-9]{64}\\z"))
                    throw new InvalidOperationException("浏览器连接码文件无效，请关闭程序后移走 browser-bridge.json，再重新启动。");
                return saved;
            }
            byte[] bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            saved = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { "token", saved } }), new UTF8Encoding(false));
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return saved;
        }

        public void Start()
        {
            lock (lifecycle)
            {
                if (disposed || Running) return;
                try
                {
                    listener.Start(); Failure = null;
                    session = new CancellationTokenSource();
                    int ticket = ++generation;
                    Task.Run(() => ListenAsync(ticket));
                }
                catch (HttpListenerException error)
                {
                    Failure = error.NativeErrorCode == 5 ?
                        "浏览器连接未启动：此 Windows 账户没有本地监听权限。划选和浮窗仍可使用；请查看网页翻译说明。" :
                        "浏览器连接未启动：本机端口不可用。划选和浮窗仍可使用。";
                }
            }
        }
        public void Stop()
        {
            lock (lifecycle)
            {
                generation++;
                if (session != null) { session.Cancel(); session.Dispose(); session = null; }
                if (listener.IsListening) listener.Stop();
            }
        }
        async Task ListenAsync(int ticket)
        {
            try
            {
                while (!shutdown.IsCancellationRequested && listener.IsListening && ticket == generation)
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    // Bound concurrent connections, including slow request bodies.
                    if (Interlocked.Increment(ref requests) > 8)
                    {
                        try { await Reply(context, 429, new { error = "翻译请求较多，请稍后重试。" }).ConfigureAwait(false); }
                        finally { try { context.Response.Close(); } catch { } Interlocked.Decrement(ref requests); }
                        continue;
                    }
                    Observe(Handle(context, ticket));
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        internal static bool IsExtensionOrigin(string origin)
        {
            return Regex.IsMatch(origin ?? "", "\\Achrome-extension://[a-p]{32}\\z");
        }
        static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var observed = failed.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        internal static bool TokenMatches(string expected, string supplied)
        {
            if (supplied == null || supplied.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
            return difference == 0;
        }
        CancellationTokenSource SessionTimeout(int ticket)
        {
            lock (lifecycle)
            {
                if (disposed || session == null || ticket != generation) return null;
                return CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, session.Token);
            }
        }
        async Task Handle(HttpListenerContext context, int ticket)
        {
            var timeoutSource = SessionTimeout(ticket);
            if (timeoutSource == null)
            {
                try { await Reply(context, 403, new { error = "浏览器连接已经关闭。" }); }
                finally { try { context.Response.Close(); } catch { } Interlocked.Decrement(ref requests); }
                return;
            }
            using (var timeout = timeoutSource)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                int errorCode = 0;
                object errorBody = null;
                try
                {
                try
                {
                    var request = context.Request;
                    string origin = request.Headers["Origin"];
                    if (!String.IsNullOrEmpty(origin) && !IsExtensionOrigin(origin))
                    { await Reply(context, 403, new { error = "此来源不能连接巴别塔。" }); return; }
                    if (!String.IsNullOrEmpty(origin))
                    {
                        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                        context.Response.Headers["Vary"] = "Origin";
                    }
                    if (request.HttpMethod == "OPTIONS")
                    {
                        if (!IsExtensionOrigin(origin)) { await Reply(context, 403, new { error = "来源不可用。" }); return; }
                        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Babel-Token";
                        await Reply(context, 204, null); return;
                    }
                    if (!TokenMatches(token, request.Headers["X-Babel-Token"]))
                    { await Reply(context, 401, new { error = "请在浏览器扩展里填写巴别塔连接码。" }); return; }
                    Options current = await readOptions();
                    if (!current.EnableBrowserBridge)
                    { await Reply(context, 403, new { error = "浏览器连接已经关闭。" }); return; }
                    string path = request.Url.AbsolutePath;
                    if (path == "/v1/status" && request.HttpMethod == "GET")
                    { await Reply(context, 200, Status(current)); return; }
                    if (request.HttpMethod != "POST" || (path != "/v1/translate" && path != "/v1/settings"))
                    { await Reply(context, 404, new { error = "接口不存在。" }); return; }
                    if (request.ContentLength64 < 0 || request.ContentLength64 > 32768 ||
                        request.Headers["Transfer-Encoding"] != null ||
                        !(request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    { await Reply(context, 413, new { error = "请求格式或长度不合适。" }); return; }
                    byte[] bytes = new byte[(int)request.ContentLength64];
                    int position = 0;
                    while (position < bytes.Length)
                    {
                        int count = await request.InputStream.ReadAsync(bytes, position, bytes.Length - position, timeout.Token);
                        if (count == 0) throw new InvalidOperationException("请求内容不完整。");
                        position += count;
                    }
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 32768, RecursionLimit = 8 };
                    var data = serializer.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(bytes));
                    if (data == null) throw new InvalidOperationException("请求内容为空。");
                    current = await readOptions();
                    if (!current.EnableBrowserBridge)
                    { await Reply(context, 403, new { error = "浏览器连接已经关闭。" }); return; }
                    object field;
                    string source = data.TryGetValue("sourceLanguage", out field) ? field as string : current.SourceLanguage;
                    string target = data.TryGetValue("targetLanguage", out field) ? field as string : current.TargetLanguage;
                    Options changed = current.Copy(); changed.SourceLanguage = source; changed.TargetLanguage = target; changed.Validate();
                    if (path == "/v1/settings")
                    {
                        await applyOptions(changed);
                        await Reply(context, 200, Status(await readOptions())); return;
                    }
                    string text = data.TryGetValue("text", out field) ? field as string : null;
                    if (String.IsNullOrWhiteSpace(text) || text.Length > 4000)
                        throw new InvalidOperationException("请每次翻译 1 到 4000 个字符。");
                    string result = await translate(text, source, target, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    if (!(await readOptions()).EnableBrowserBridge || ticket != generation)
                        throw new OperationCanceledException();
                    await Reply(context, 200, new { text = result, sourceLanguage = source, targetLanguage = target });
                }
                catch (OperationCanceledException) { errorCode = 408; errorBody = new { error = "翻译已取消或等待超时，原文保留。" }; }
                catch (Exception) { errorCode = 400; errorBody = new { error = "翻译未完成，请检查语言设置或本地模型，原文保留。" }; }
                if (errorCode != 0) await Reply(context, errorCode, errorBody);
                }
                finally
                {
                    try { context.Response.Close(); } catch { }
                    Interlocked.Decrement(ref requests);
                }
            }
        }
        static object Status(Options options)
        {
            var languages = new List<object>();
            foreach (var language in LanguageChoice.All) languages.Add(new { name = language.Name, code = language.Code });
            return new { version = "0.2.0", sourceLanguage = options.SourceLanguage, targetLanguage = options.TargetLanguage,
                languages = languages.ToArray(), local = true };
        }
        static async Task Reply(HttpListenerContext context, int code, object data)
        {
            try
            {
                context.Response.StatusCode = code;
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                byte[] body = data == null ? new byte[0] : Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(data));
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }
        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true; shutdown.Cancel(); listener.Close();
        }
    }
}
