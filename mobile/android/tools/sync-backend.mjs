/**
 * 从电脑端 NativeBackend 源码生成 Android 专用的 ArtifactTools。
 *
 * 为什么需要这一步：整个 ArtifactTools（文件名清洗、自然排序、ZIP、PDF 写入）
 * 都是平台无关的，只有繁简转换用的 Win32 LCMapStringEx 在 Android 上不存在。
 * 与其复制 600 行代码，这里只把那一处替换成 Android 的 ICU 实现，
 * 其余部分逐字保持一致 —— 电脑端改了什么，重新跑一次就能同步过来。
 *
 *   node mobile/android/tools/sync-backend.mjs
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = resolve(here, '..', '..', '..', 'NativeBackend', 'ArtifactTools.cs');
const target = resolve(here, '..', 'Backend', 'ArtifactTools.cs');

const original = readFileSync(source, 'utf8');
const eol = original.includes('\r\n') ? '\r\n' : '\n';
let text = original.replace(/\r\n/g, '\n');

/** 把一段多行文本改写成统一的 \n 形式，便于精确匹配。 */
const anchor = value => value.replace(/\r\n/g, '\n');

function replaceOnce(haystack, from, to, label) {
  const needle = anchor(from);
  const first = haystack.indexOf(needle);
  if (first < 0) throw new Error(`sync-backend: 没有找到锚点「${label}」，电脑端源码可能已经改动`);
  if (haystack.indexOf(needle, first + 1) >= 0) throw new Error(`sync-backend: 锚点「${label}」出现多次，无法安全替换`);
  return haystack.slice(0, first) + to + haystack.slice(first + needle.length);
}

/* 1. 去掉 P/Invoke 声明与只服务于它的常量 */
text = replaceOnce(text, `
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(
        string? lpLocaleName, uint dwMapFlags,
        string lpSrcStr, int cchSrc,
        [Out] char[]? lpDestStr, int cchDest,
        IntPtr lpVersionInfo, IntPtr lpReserved, IntPtr sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;
`, '', 'LCMapStringEx 声明');

/* 2. 方法体改为调用 Android 的 ICU 实现 */
text = replaceOnce(text, `
        var flag = toSimplified ? LCMAP_SIMPLIFIED_CHINESE : LCMAP_TRADITIONAL_CHINESE;
        var dest = new char[text.Length];
        var len = LCMapStringEx("zh-Hans", flag, text, text.Length, dest, dest.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return len > 0 ? new string(dest, 0, len) : text;
`, `
        return MobileConverter is null ? text : MobileConverter(text, toSimplified);
`, 'ConvertChineseScript 方法体');

/* 3. 注入移动端转换器，避免直接依赖某个具体类名 */
text = replaceOnce(text, 'public static class ArtifactTools\n{', `public static class ArtifactTools
{
    /// <summary>Android 侧注入的繁简转换实现（见 Backend/ChineseScript.cs）。</summary>
    internal static Func<string, bool, string>? MobileConverter;
`, 'ArtifactTools 类声明');

const header = `// ─────────────────────────────────────────────────────────────────────────────
// 本文件由 mobile/android/tools/sync-backend.mjs 从电脑端 NativeBackend/ArtifactTools.cs
// 生成，请勿手改。改动请落在电脑端源码，然后重新运行：
//     node mobile/android/tools/sync-backend.mjs
// ─────────────────────────────────────────────────────────────────────────────
`;

writeFileSync(target, (header + text).replace(/\n/g, eol), 'utf8');
console.log(`synced  ${source}`);
console.log(`     -> ${target}  (${text.split('\n').length} 行)`);
