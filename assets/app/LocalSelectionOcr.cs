using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SwipeTranslate
{
    /// <summary>
    /// Runs the installed Windows OCR engine on a supplied selection-only bitmap.
    /// This class never captures the screen, reads the clipboard, sends a request,
    /// or writes an image to disk. Call TryRead from the selection worker.
    /// Reflection keeps the .NET Framework 4.8 build independent of SDK winmd files.
    /// </summary>
    public static class LocalSelectionOcr
    {
        private const int MaximumTextLength = 4000;
        private const int MaximumInputPixels = 12000000;
        private const int RecognitionTimeoutMilliseconds = 3500;
        private static readonly SemaphoreSlim RecognitionGate = new SemaphoreSlim(1, 1);
        private static readonly Lazy<RuntimeState> Runtime = new Lazy<RuntimeState>(
            delegate { return new RuntimeState(); }, LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool TryGetAvailableLanguages(out string languages, out string reason)
        {
            languages = String.Empty;
            reason = String.Empty;
            try
            {
                languages = String.Join(", ", Runtime.Value.LanguageTags.ToArray());
                if (languages.Length == 0)
                {
                    reason = "本机没有可用的 Windows OCR 识别语言。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                reason = DescribeError(exception);
                return false;
            }
        }

        public static bool TryRead(Bitmap image, out string text, out string reason)
        {
            // Preserve the original English-only entry point for older callers.
            return TryRead(image, "en", "zh-CN", out text, out reason);
        }

        public static bool TryRead(Bitmap image, string sourceLanguage, string targetLanguage,
            out string text, out string reason)
        {
            text = String.Empty;
            reason = String.Empty;
            if (image == null)
            {
                reason = "没有供识别的选区图像。";
                return false;
            }
            if (!RecognitionGate.Wait(0))
            {
                reason = "本地文字识别仍在处理上一选区。";
                return false;
            }

            Bitmap ownedImage = null;
            bool workerOwnsGate = false;
            try
            {
                if (image.Width < 2 || image.Height < 2 ||
                    (long)image.Width * image.Height > MaximumInputPixels)
                {
                    reason = "选区图像尺寸不适合本地文字识别。";
                    return false;
                }

                // The worker owns its copy: a timed-out caller may immediately
                // dispose the selection snapshot without racing native OCR.
                RuntimeState runtime = Runtime.Value;
                string languageTag;
                if (!TrySelectLanguageTag(runtime.LanguageTags, sourceLanguage, targetLanguage,
                    out languageTag, out reason))
                    return false;
                object language = runtime.Languages[languageTag];
                ownedImage = PrepareImage(image, runtime.MaximumImageDimension);
                Bitmap workerImage = ownedImage;
                Task<string> work = Task.Run(async delegate
                {
                    try
                    {
                        using (workerImage)
                            return await RecognizeAsync(workerImage, language, languageTag).ConfigureAwait(false);
                    }
                    finally
                    {
                        RecognitionGate.Release();
                    }
                });
                workerOwnsGate = true;
                ownedImage = null;

                // Observe a late fault as well, without storing image or text.
                work.ContinueWith(delegate(Task<string> failed) { var observed = failed.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                if (!work.Wait(RecognitionTimeoutMilliseconds))
                {
                    reason = "本地文字识别超时，请缩小选区后重试。";
                    return false;
                }

                text = (work.Result ?? String.Empty).Trim();
                if (text.Length == 0)
                {
                    reason = "选区内没有识别到可翻译文字。";
                    return false;
                }
                if (text.Length > MaximumTextLength)
                {
                    text = String.Empty;
                    reason = "选中文字超过 4000 字，请缩小选区。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                reason = DescribeError(exception);
                return false;
            }
            finally
            {
                if (ownedImage != null)
                    ownedImage.Dispose();
                if (!workerOwnsGate)
                    RecognitionGate.Release();
            }
        }

        // This selection is deterministic and contains no image or user text.
        // An OCR engine recognizes a source language; the translation target is
        // only a hint when the source remains automatic. In that case English
        // targets prefer simplified Chinese, and Chinese targets prefer English.
        internal static bool TrySelectLanguageTag(IList<string> installed, string sourceLanguage,
            string targetLanguage, out string tag, out string reason)
        {
            tag = String.Empty;
            reason = String.Empty;
            string source = NormalizeLanguage(sourceLanguage);
            string target = NormalizeLanguage(targetLanguage);
            if (source.Length == 0 || source == "auto")
            {
                if (PrimaryLanguage(target) == "en") source = "zh-CN";
                else if (PrimaryLanguage(target) == "zh") source = "en";
                else
                {
                    reason = "请先选择原文语言，本地 OCR 无法仅从这个目标语言确定识别语言。";
                    return false;
                }
            }
            source = NormalizeLanguage(source);
            if (installed == null || installed.Count == 0)
            {
                reason = "本机没有可用的 Windows OCR 识别语言。";
                return false;
            }

            bool simplified = IsSimplifiedChinese(source) || source == "zh";
            bool traditional = IsTraditionalChinese(source);
            if (simplified || traditional)
            {
                string[] preferred = simplified
                    ? new string[] { source, "zh-Hans-CN", "zh-CN", "zh-Hans", "zh-SG" }
                    : new string[] { source, "zh-Hant-TW", "zh-TW", "zh-Hant", "zh-HK", "zh-MO" };
                foreach (string candidate in preferred)
                {
                    foreach (string available in installed)
                    {
                        string normalized = NormalizeLanguage(available);
                        if (String.Equals(normalized, NormalizeLanguage(candidate), StringComparison.Ordinal) &&
                            (simplified ? IsSimplifiedChinese(normalized) : IsTraditionalChinese(normalized)))
                        { tag = available; return true; }
                    }
                }
                foreach (string available in installed)
                {
                    string normalized = NormalizeLanguage(available);
                    if (simplified ? IsSimplifiedChinese(normalized) : IsTraditionalChinese(normalized))
                    { tag = available; return true; }
                }
                reason = "本机未安装" + (simplified ? "简体中文" : "繁体中文") +
                    " Windows OCR 识别语言，无法识别此语言的蓝色选区。";
                return false;
            }

            foreach (string available in installed)
                if (NormalizeLanguage(available) == source)
                { tag = available; return true; }
            if (PrimaryLanguage(source) == "en")
            {
                foreach (string available in installed)
                    if (NormalizeLanguage(available) == "en-us")
                    { tag = available; return true; }
                foreach (string available in installed)
                    if (PrimaryLanguage(NormalizeLanguage(available)) == "en")
                    { tag = available; return true; }
            }
            // A generic language code can use any installed regional variant.
            // For other explicitly qualified scripts/regions require a match.
            else if (source.IndexOf('-') < 0 && source != "zh")
            {
                foreach (string available in installed)
                    if (PrimaryLanguage(NormalizeLanguage(available)) == source)
                    { tag = available; return true; }
            }
            reason = "本机未安装" + LanguageName(source) + " Windows OCR 识别语言（" + source +
                "），无法识别此语言的蓝色选区。";
            return false;
        }

        private static string NormalizeLanguage(string language)
        {
            return (language ?? String.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        }

        private static string PrimaryLanguage(string language)
        {
            int separator = language.IndexOf('-');
            return separator < 0 ? language : language.Substring(0, separator);
        }

        private static bool IsSimplifiedChinese(string language)
        {
            return language == "zh-cn" || language == "zh-sg" || language == "zh-hans" ||
                language.StartsWith("zh-hans-", StringComparison.Ordinal);
        }

        private static bool IsTraditionalChinese(string language)
        {
            return language == "zh-tw" || language == "zh-hk" || language == "zh-mo" || language == "zh-hant" ||
                language.StartsWith("zh-hant-", StringComparison.Ordinal);
        }

        private static string LanguageName(string language)
        {
            switch (PrimaryLanguage(language))
            {
                case "en": return "英文";
                case "zh": return "中文";
                case "ja": return "日文";
                case "ko": return "韩文";
                case "fr": return "法文";
                case "de": return "德文";
                case "es": return "西班牙文";
                case "ru": return "俄文";
                default: return "指定语言的";
            }
        }

        private static Bitmap PrepareImage(Bitmap source, int maximumDimension)
        {
            if (maximumDimension < 64)
                throw new PlatformNotSupportedException("Windows OCR 图像尺寸能力不可用。");

            // Small screen text benefits from two-times scaling. Fit both axes
            // inside the engine's actual limit rather than assuming a fixed size.
            double scale = source.Width <= 1600 && source.Height <= 1000 ? 2.0 : 1.0;
            scale = Math.Min(scale, (double)(maximumDimension - 16) / source.Width);
            scale = Math.Min(scale, (double)(maximumDimension - 16) / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            Bitmap prepared = new Bitmap(width + 16, height + 16, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(prepared))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(8, 8, width, height),
                        new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
                }
                return prepared;
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
        }

        private static async Task<string> RecognizeAsync(Bitmap image, object language, string languageTag)
        {
            RuntimeState runtime = Runtime.Value;
            object engine = runtime.CreateEngine.Invoke(null, new object[] { language });
            if (engine == null)
                throw new PlatformNotSupportedException("无法创建本机 Windows OCR 引擎（" + languageTag + "）。");

            object randomAccessStream = null;
            object softwareBitmap = null;
            using (MemoryStream encodedImage = new MemoryStream())
            {
                image.Save(encodedImage, ImageFormat.Png);
                encodedImage.Position = 0;
                try
                {
                    randomAccessStream = runtime.AsRandomAccessStream.Invoke(null, new object[] { encodedImage });
                    object decoderOperation = runtime.CreateDecoder.Invoke(null, new object[] { randomAccessStream });
                    object decoder = await runtime.AwaitOperation(decoderOperation, runtime.DecoderType).ConfigureAwait(false);
                    object bitmapOperation = runtime.GetSoftwareBitmap.Invoke(decoder,
                        new object[] { runtime.Bgra8, runtime.IgnoreAlpha });
                    softwareBitmap = await runtime.AwaitOperation(bitmapOperation, runtime.SoftwareBitmapType).ConfigureAwait(false);
                    object ocrOperation = runtime.Recognize.Invoke(engine, new object[] { softwareBitmap });
                    object result = await runtime.AwaitOperation(ocrOperation, runtime.ResultType).ConfigureAwait(false);

                    // Keep line boundaries instead of merging neighbouring words.
                    StringBuilder text = new StringBuilder();
                    IEnumerable lines = runtime.ResultLines.GetValue(result, null) as IEnumerable;
                    if (lines != null)
                    {
                        foreach (object line in lines)
                        {
                            string lineText = runtime.LineText.GetValue(line, null) as string;
                            if (String.IsNullOrWhiteSpace(lineText))
                                continue;
                            if (text.Length > 0)
                                text.Append('\n');
                            text.Append(lineText.Trim());
                        }
                    }
                    return text.Length > 0 ? text.ToString() : runtime.ResultText.GetValue(result, null) as string;
                }
                finally
                {
                    DisposeProjected(softwareBitmap);
                    DisposeProjected(randomAccessStream);
                }
            }
        }

        private static void DisposeProjected(object value)
        {
            IDisposable disposable = value as IDisposable;
            if (disposable != null)
                disposable.Dispose();
        }

        private static string DescribeError(Exception exception)
        {
            while (exception is TargetInvocationException || exception is AggregateException)
            {
                if (exception.InnerException == null)
                    break;
                exception = exception.InnerException;
            }
            return "本地 OCR 不可用：" + exception.GetType().Name + "：" + exception.Message;
        }

        private sealed class RuntimeState
        {
            public readonly List<string> LanguageTags = new List<string>();
            public readonly Dictionary<string, object> Languages = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            public readonly int MaximumImageDimension;
            public readonly Type DecoderType;
            public readonly Type SoftwareBitmapType;
            public readonly Type ResultType;
            public readonly MethodInfo CreateEngine;
            public readonly MethodInfo AsRandomAccessStream;
            public readonly MethodInfo CreateDecoder;
            public readonly MethodInfo GetSoftwareBitmap;
            public readonly MethodInfo Recognize;
            public readonly PropertyInfo ResultLines;
            public readonly PropertyInfo ResultText;
            public readonly PropertyInfo LineText;
            public readonly object Bgra8;
            public readonly object IgnoreAlpha;
            private readonly MethodInfo AsTask;

            public RuntimeState()
            {
                Assembly bridge;
                try
                {
                    bridge = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                }
                catch (FileNotFoundException)
                {
                    bridge = Assembly.LoadFrom(Path.Combine(
                        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.WindowsRuntime.dll"));
                }

                Type engineType = GetWinRtType("Windows.Media.Ocr.OcrEngine");
                Type languageType = GetWinRtType("Windows.Globalization.Language");
                Type randomStreamType = GetWinRtType("Windows.Storage.Streams.IRandomAccessStream");
                DecoderType = GetWinRtType("Windows.Graphics.Imaging.BitmapDecoder");
                SoftwareBitmapType = GetWinRtType("Windows.Graphics.Imaging.SoftwareBitmap");
                ResultType = GetWinRtType("Windows.Media.Ocr.OcrResult");
                Type lineType = GetWinRtType("Windows.Media.Ocr.OcrLine");
                Type pixelFormatType = GetWinRtType("Windows.Graphics.Imaging.BitmapPixelFormat");
                Type alphaModeType = GetWinRtType("Windows.Graphics.Imaging.BitmapAlphaMode");

                IEnumerable languages = engineType.GetProperty("AvailableRecognizerLanguages").GetValue(null, null) as IEnumerable;
                if (languages != null)
                {
                    foreach (object language in languages)
                    {
                        string tag = languageType.GetProperty("LanguageTag").GetValue(language, null) as string;
                        if (!String.IsNullOrEmpty(tag) && !Languages.ContainsKey(tag))
                        {
                            LanguageTags.Add(tag);
                            Languages.Add(tag, language);
                        }
                    }
                }

                MaximumImageDimension = Convert.ToInt32(engineType.GetProperty("MaxImageDimension").GetValue(null, null));
                CreateEngine = engineType.GetMethod("TryCreateFromLanguage", new Type[] { languageType });
                Recognize = engineType.GetMethod("RecognizeAsync", new Type[] { SoftwareBitmapType });
                CreateDecoder = DecoderType.GetMethod("CreateAsync", new Type[] { randomStreamType });
                GetSoftwareBitmap = DecoderType.GetMethod("GetSoftwareBitmapAsync", new Type[] { pixelFormatType, alphaModeType });
                ResultLines = ResultType.GetProperty("Lines");
                ResultText = ResultType.GetProperty("Text");
                LineText = lineType.GetProperty("Text");
                Bgra8 = Enum.Parse(pixelFormatType, "Bgra8");
                IgnoreAlpha = Enum.Parse(alphaModeType, "Ignore");
                AsRandomAccessStream = bridge.GetType("System.IO.WindowsRuntimeStreamExtensions", true)
                    .GetMethod("AsRandomAccessStream", new Type[] { typeof(Stream) });

                foreach (MethodInfo candidate in bridge.GetType("System.WindowsRuntimeSystemExtensions", true).GetMethods())
                {
                    if (candidate.Name != "AsTask" || !candidate.IsGenericMethodDefinition ||
                        candidate.GetGenericArguments().Length != 1 || candidate.GetParameters().Length != 1)
                        continue;
                    Type parameterType = candidate.GetParameters()[0].ParameterType;
                    if (parameterType.IsGenericType &&
                        parameterType.GetGenericTypeDefinition().FullName == "Windows.Foundation.IAsyncOperation`1")
                    {
                        AsTask = candidate;
                        break;
                    }
                }
                if (AsTask == null || CreateEngine == null || Recognize == null || CreateDecoder == null ||
                    GetSoftwareBitmap == null || AsRandomAccessStream == null)
                    throw new PlatformNotSupportedException("缺少 Windows OCR 所需的 WinRT 接口。");
            }

            public async Task<object> AwaitOperation(object operation, Type resultType)
            {
                Task task = (Task)AsTask.MakeGenericMethod(resultType).Invoke(null, new object[] { operation });
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result").GetValue(task, null);
            }

            private static Type GetWinRtType(string fullName)
            {
                return Type.GetType(fullName + ", Windows.Foundation, ContentType=WindowsRuntime", true);
            }
        }
    }
}

#if LOCAL_OCR_SELF_TEST
internal static class LocalSelectionOcrSyntheticTest
{
    private static int Main()
    {
        string languages;
        string reason;
        if (!SwipeTranslate.LocalSelectionOcr.TryGetAvailableLanguages(out languages, out reason))
        {
            Console.WriteLine(reason);
            return 1;
        }
        Console.WriteLine("Installed OCR languages: " + languages);
        bool passed = Test("black-on-white", Color.White, Color.Black,
            "This is a large job, so I will work in stages.");
        passed &= Test("white-on-blue", Color.FromArgb(0, 120, 215), Color.White,
            "Select this sentence to see the Chinese translation.");
        passed &= Test("selection-mask-two-lines", Color.White, Color.Black,
            "Only selected words are recognized.\nTranslation stays on this computer.");
        return passed ? 0 : 1;
    }

    private static bool Test(string name, Color background, Color foreground, string expected)
    {
        using (Bitmap bitmap = new Bitmap(900, expected.Contains("\n") ? 95 : 55))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (Font font = new Font("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush brush = new SolidBrush(foreground))
        {
            graphics.Clear(background);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(expected, font, brush, new PointF(12, 10), StringFormat.GenericTypographic);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string text;
            string reason;
            bool recognized = SwipeTranslate.LocalSelectionOcr.TryRead(bitmap, out text, out reason);
            clock.Stop();
            string normalizedExpected = System.Text.RegularExpressions.Regex.Replace(expected, "\\s+", " ").Trim();
            string normalizedActual = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            bool passed = recognized && String.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal);
            Console.WriteLine(name + " " + (passed ? "PASS" : "FAIL") + " " + clock.ElapsedMilliseconds + "ms");
            Console.WriteLine(recognized ? text : reason);
            return passed;
        }
    }
}
#endif
