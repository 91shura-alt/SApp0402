using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace SellerOps.App.Services
{
    /// <summary>
    /// Озвучка без зависимости от System.Speech:
    /// 1) Пытаемся найти тип System.Speech.Synthesis.SpeechSynthesizer динамически (если вдруг есть).
    /// 2) Если его нет — используем COM SAPI.SpVoice через Reflection (работает на любой Windows).
    /// Предупреждение CA1416 подавлено атрибутом для Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class Speech
    {
        private static readonly object _lock = new();

        // Кэш для выбранного механизма
        private static Type? _speechSynthType;          // System.Speech... если найдём
        private static object? _speechSynthInstance;    // экземпляр SpeechSynthesizer
        private static Type? _sapiType;                 // COM тип SAPI.SpVoice
        private static object? _sapiInstance;           // экземпляр SpVoice

        /// <summary>Асинхронно проговаривает текст. Без исключений наружу.</summary>
        public static Task SayAsync(string text)
        {
#pragma warning disable CA1416
            if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
            return Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        // 1) Попробуем System.Speech, если вдруг установлен reference-pack
                        if (EnsureSystemSpeech())
                        {
                            // Synth.Speak(text)
                            _speechSynthType!.GetMethod("Speak", new[] { typeof(string) })!
                                .Invoke(_speechSynthInstance, new object[] { text });
                            return;
                        }

                        // 2) Иначе — COM SAPI.SpVoice (есть в любой Windows)
                        if (EnsureSapiVoice())
                        {
                            // speak.InvokeMember("Speak", ...)
                            _sapiType!.InvokeMember("Speak",
                                BindingFlags.InvokeMethod, null, _sapiInstance,
                                new object[] { text });
                        }
                    }
                }
                catch
                {
                    // молча игнорируем — озвучка не критична
                }
            });
#pragma warning restore CA1416
        }

        // Пытается создать System.Speech.Synthesis.SpeechSynthesizer без compile-time зависимости.
        private static bool EnsureSystemSpeech()
        {
            try
            {
                if (_speechSynthInstance != null) return true;

                // Ищем тип по полному имени
                _speechSynthType ??= Type.GetType(
                    "System.Speech.Synthesis.SpeechSynthesizer, System.Speech",
                    throwOnError: false, ignoreCase: false);

                if (_speechSynthType == null) return false;

                _speechSynthInstance = Activator.CreateInstance(_speechSynthType);

                // Настройки: выбрать русскую, если есть; иначе оставить дефолт
                try
                {
                    var getVoices = _speechSynthType.GetMethod("GetInstalledVoices", Type.EmptyTypes);
                    var voices = getVoices?.Invoke(_speechSynthInstance, Array.Empty<object>()) as System.Collections.IEnumerable;
                    if (voices != null)
                    {
                        foreach (var v in voices)
                        {
                            // v.VoiceInfo.Name / Culture
                            var voiceInfo = v.GetType().GetProperty("VoiceInfo")?.GetValue(v);
                            var name = voiceInfo?.GetType().GetProperty("Name")?.GetValue(voiceInfo) as string ?? "";
                            var culture = voiceInfo?.GetType().GetProperty("Culture")?.GetValue(voiceInfo)?.ToString() ?? "";

                            if (name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                                culture.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                            {
                                _speechSynthType.GetMethod("SelectVoice", new[] { typeof(string) })?
                                    .Invoke(_speechSynthInstance, new object[] { name });
                                break;
                            }
                        }
                    }

                    // Rate/Volume
                    _speechSynthType.GetProperty("Rate")?.SetValue(_speechSynthInstance, 0);
                    _speechSynthType.GetProperty("Volume")?.SetValue(_speechSynthInstance, 100);
                }
                catch { /* безопасный дефолт */ }

                return true;
            }
            catch
            {
                _speechSynthInstance = null;
                _speechSynthType = null;
                return false;
            }
        }

        // Создаёт COM-объект SAPI.SpVoice без каких-либо ссылок на System.Speech
        private static bool EnsureSapiVoice()
        {
            try
            {
                if (_sapiInstance != null) return true;

                _sapiType = Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: false);
                if (_sapiType == null) return false;

                _sapiInstance = Activator.CreateInstance(_sapiType);

                // Зададим громкость/скорость, если свойства доступны
                try
                {
                    _sapiType.InvokeMember("Rate", BindingFlags.SetProperty, null, _sapiInstance, new object[] { 0 });
                    _sapiType.InvokeMember("Volume", BindingFlags.SetProperty, null, _sapiInstance, new object[] { 100 });
                }
                catch { /* не критично */ }

                return true;
            }
            catch
            {
                _sapiInstance = null;
                _sapiType = null;
                return false;
            }
        }
    }
}
