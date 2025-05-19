using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diva.FontSubsetting;
using QuestPDF.Drawing;
using SkiaSharp;

namespace QuestPDF.Helpers;

public static class FontManagerHelper
{
    // FontManagerへのアクセスを同期化するためのロックオブジェクト
    private static readonly object LockObject = new();

    /// <summary>
    /// サブセットフォントを生成して登録します。
    /// </summary>
    /// <param name="fontBytes">フォントデータ</param>
    /// <param name="subsetString">サブセットフォントに含める文字</param>
    /// <param name="suffix">サブセットフォントのフォントファミリー名の接尾辞（未エンコード）</param>
    /// <param name="includesAsciiPrintableCharacters">サブセットにASCII印刷可能文字を含める</param>
    public static void RegisterFont(
        byte[] fontBytes,
        string subsetString,
        string? suffix = null,
        bool includesAsciiPrintableCharacters = true)
    {
        var subsetFonts = FontSubsetter.SubsetFonts(fontBytes, subsetString, suffix, includesAsciiPrintableCharacters);

        lock (LockObject)
        {
            foreach (var subsetFont in subsetFonts)
            {
                using var stream = new MemoryStream(subsetFont);
                FontManager.RegisterFont(stream);
            }
        }
    }

    /// <summary>
    /// フォントファミリー名を指定して、登録済みのフォントからサブセットフォントを作成し登録します。
    /// </summary>
    /// <param name="fontName">登録済みのフォントファミリー名</param>
    /// <param name="subsetString">サブセットフォントに含める文字</param>
    /// <param name="suffix">サブセットフォントのフォントファミリー名の接尾辞（未エンコード）</param>
    /// <param name="includesAsciiPrintableCharacters">サブセットにASCII印刷可能文字を含める</param>
    public static void RegisterFont(
        string fontName,
        string subsetString,
        string? suffix = null,
        bool includesAsciiPrintableCharacters = true)
    {
        var fontBytes = GetFonts(fontName);
        foreach (var bytes in fontBytes)
        {
            RegisterFont(bytes, subsetString, suffix, includesAsciiPrintableCharacters);
        }
    }

    /// <summary>
    /// 既存のサブセットフォントを削除し、新しいサブセットフォントを登録します。
    /// </summary>
    /// <param name="fontBytes">フォントデータ</param>
    /// <param name="subsetString">サブセットフォントに含める文字</param>
    /// <param name="suffix">サブセットフォントのフォントファミリー名の接尾辞（未エンコード）</param>
    /// <param name="includesAsciiPrintableCharacters">サブセットにASCII印刷可能文字を含める</param>
    public static void UpdateFont(
        byte[] fontBytes,
        string subsetString,
        string suffix,
        bool includesAsciiPrintableCharacters = true)
    {
        // サブセット生成は計算コストが高いのでlockの外で実行
        var subsetFonts = FontSubsetter.SubsetFonts(fontBytes, subsetString, suffix, includesAsciiPrintableCharacters);

        lock (LockObject)
        {
            // 既存のフォントを削除
            RemoveFontsInternal(x => x.EndsWith($"+{FontSubsetter.EncodeSuffix(suffix)}"));

            // 新しいフォントを登録
            foreach (var subsetFont in subsetFonts)
            {
                using var stream = new MemoryStream(subsetFont);
                FontManager.RegisterFont(stream);
            }
        }
    }

    /// <summary>
    /// フォントファミリー名からフォントデータを取得します。
    /// </summary>
    /// <param name="fontName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static byte[][] GetFonts(string fontName)
    {
        // 指定されたフォントファミリー名に一致するFontManagerのFontStyleSetを取得
        var styleSet = GetStyleSet();
        var key = styleSet.Keys
            .OfType<string>()
            .SingleOrDefault(x => x == fontName);

        if (key == null)
            throw new InvalidOperationException($"Font '{fontName}' not found in StyleSets.");

        var fontStyleSetType = styleSet.GetType().GenericTypeArguments[1];
        var fontStyleSet = styleSet[key];
        if (fontStyleSet == null || !fontStyleSetType.IsInstanceOfType(fontStyleSet))
        {
            throw new InvalidOperationException($"FontStyleSet for '{fontName}' is null or invalid.");
        }

        // FontStyleSetのStylesからすべてのSKTypefaceを取得
        var skTypefaces = fontStyleSet
            .GetType()
            .GetProperty("Styles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(fontStyleSet) as IDictionary;

        if (skTypefaces == null)
            throw new InvalidOperationException("FontStyleSet.Styles is null.");

        var typefaces = skTypefaces.Values.OfType<SKTypeface>().ToList();

        // SKTypefaceからフォントデータを取得
        if (typefaces.Count <= 0)
            throw new InvalidOperationException($"No SKTypeface found for font '{fontName}'.");

        var fontDataList = new List<byte[]>();
        foreach (var typeface in typefaces)
        {
            using var fontStream = typeface.OpenStream();
            if (fontStream is not { Length: > 0 })
                continue;

            var fontData = new byte[fontStream.Length];
            fontStream.Read(fontData, fontData.Length);
            fontDataList.Add(fontData);
        }

        return fontDataList.ToArray();
    }

    /// <summary>
    /// lock内で実行される内部メソッド
    /// </summary>
    private static void RemoveFontsInternal(Func<string, bool> fontNamePredicate)
    {
        // FontManagerのStyleSetsを取得して、条件に合致するものを削除する
        var styleSet = GetStyleSet();
        var keysToRemove = styleSet.Keys
            .OfType<string>()
            .Where(fontNamePredicate)
            .ToList();

        var styleSetType = styleSet.GetType();
        var tryRemoveMethod = styleSetType
            .GetMethod(
                "TryRemove",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                [
                    typeof(string),
                    styleSetType.GenericTypeArguments[1].MakeByRefType()
                ]
            ) ?? throw new InvalidOperationException("FontManager.StyleSets.TryRemove is null");

        foreach (var key in keysToRemove)
        {
            tryRemoveMethod.Invoke(styleSet, [key, null]);
        }
    }

    /// <summary>
    /// FontManagerのStyleSetsを取得します。
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static IDictionary GetStyleSet()
    {
        var fontManagerType = typeof(FontManager);
        var styleSetField = fontManagerType.GetField("StyleSets",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        return styleSetField?.GetValue(null) as IDictionary
               ?? throw new InvalidOperationException("FontManager.StyleSets is null");
    }

    /// <summary>
    /// 指定の名前に完全一致するフォントを削除します。
    /// </summary>
    /// <param name="fontName">削除するフォントファミリー名</param>
    public static void RemoveSubsetFontByName(string fontName)
    {
        lock (LockObject)
        {
            RemoveFontsInternal(x => x == fontName);
        }
    }

    /// <summary>
    /// 指定のsuffixの付くサブセットフォントを削除します。
    /// </summary>
    /// <param name="suffix">サブセットフォントのフォントファミリー名の接尾辞（未エンコード）</param>
    /// <remarks>
    /// このメソッドは、フォントファミリー名が「+エンコードされた接尾辞」で終わるフォントを削除します。
    /// 内部では <c>x.EndsWith($"+{encodeSuffix}")</c> の条件で一致するフォントを特定しています。
    /// </remarks>
    public static void RemoveSubsetFontsBySuffix(string suffix)
    {
        lock (LockObject)
        {
            var encodeSuffix = FontSubsetter.EncodeSuffix(suffix);
            RemoveFontsInternal(x => x.EndsWith($"+{encodeSuffix}"));
        }
    }
}