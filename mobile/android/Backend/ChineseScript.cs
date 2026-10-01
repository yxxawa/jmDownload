using Android.Runtime;

namespace DesktopShell.NativeBackend;

/// <summary>
/// 电脑端用 Win32 的 LCMapStringEx 做繁简转换，Android 没有这个 API。
/// 这里改用 Android 自带的 ICU 音译器（android.icu.text.Transliterator），
/// 走 JNI 调用可以避免依赖具体版本的托管绑定；任何一步失败都退回原文，
/// 不会让文件命名流程直接崩掉。
/// </summary>
internal static class ChineseScript
{
    private const string TransliteratorClass = "android/icu/text/Transliterator";
    private const string TraditionalToSimplified = "Traditional-Simplified";
    private const string SimplifiedToTraditional = "Simplified-Traditional";

    private static IntPtr _classHandle;
    private static IntPtr _getInstanceId;
    private static IntPtr _transliterateId;
    private static bool _unavailable;

    public static string Convert(string text, bool toSimplified)
    {
        if (string.IsNullOrEmpty(text) || _unavailable) return text;
        try
        {
            if (!EnsureHandles()) return text;
            var id = toSimplified ? TraditionalToSimplified : SimplifiedToTraditional;

            var javaId = JNIEnv.NewString(id);
            var instance = JNIEnv.CallStaticObjectMethod(_classHandle, _getInstanceId, new JValue(javaId));
            JNIEnv.DeleteLocalRef(javaId);
            if (instance == IntPtr.Zero) return text;

            try
            {
                var javaText = JNIEnv.NewString(text);
                var converted = JNIEnv.CallObjectMethod(instance, _transliterateId, new JValue(javaText));
                JNIEnv.DeleteLocalRef(javaText);
                if (converted == IntPtr.Zero) return text;
                var result = JNIEnv.GetString(converted, JniHandleOwnership.DoNotTransfer);
                JNIEnv.DeleteLocalRef(converted);
                return string.IsNullOrEmpty(result) ? text : result;
            }
            finally
            {
                JNIEnv.DeleteLocalRef(instance);
            }
        }
        catch (Exception exception)
        {
            _unavailable = true;
            Android.Util.Log.Warn("JmDownload", "繁简转换不可用，保留原文: " + exception.Message);
            return text;
        }
    }

    private static bool EnsureHandles()
    {
        if (_classHandle != IntPtr.Zero) return true;
        _classHandle = JNIEnv.FindClass(TransliteratorClass);
        if (_classHandle == IntPtr.Zero) { _unavailable = true; return false; }
        _getInstanceId = JNIEnv.GetStaticMethodID(_classHandle, "getInstance", "(Ljava/lang/String;)Landroid/icu/text/Transliterator;");
        _transliterateId = JNIEnv.GetMethodID(_classHandle, "transliterate", "(Ljava/lang/String;)Ljava/lang/String;");
        if (_getInstanceId == IntPtr.Zero || _transliterateId == IntPtr.Zero) { _unavailable = true; return false; }
        return true;
    }
}
