using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SilkyUISupport;

/// <summary>解析 XNA Framework Color 类型支持的颜色格式。</summary>
internal static class XnaColorParser
{
    private static readonly Regex HexPattern = new Regex(@"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex RgbPattern = new Regex(@"^rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RgbaPattern = new Regex(@"^rgba\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>尝试解析颜色值为 ARGB 分量。</summary>
    /// <param name="value">颜色字符串</param>
    /// <param name="alpha">Alpha 通道 (0-255)</param>
    /// <param name="red">红色通道 (0-255)</param>
    /// <param name="green">绿色通道 (0-255)</param>
    /// <param name="blue">蓝色通道 (0-255)</param>
    /// <returns>是否成功解析</returns>
    public static bool TryParse(string value, out byte alpha, out byte red, out byte green, out byte blue)
    {
        alpha = 255;
        red = green = blue = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        // #RRGGBB 或 #RRGGBBAA
        var hexMatch = HexPattern.Match(value);
        if (hexMatch.Success)
        {
            var hex = hexMatch.Groups[1].Value;
            if (hex.Length == 6)
            {
                // #RRGGBB
                red = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                green = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                blue = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                alpha = 255;
                return true;
            }
            else if (hex.Length == 8)
            {
                // #RRGGBBAA
                red = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                green = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                blue = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                alpha = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
                return true;
            }
        }

        // rgb(r, g, b)
        var rgbMatch = RgbPattern.Match(value);
        if (rgbMatch.Success)
        {
            if (byte.TryParse(rgbMatch.Groups[1].Value, out red) &&
                byte.TryParse(rgbMatch.Groups[2].Value, out green) &&
                byte.TryParse(rgbMatch.Groups[3].Value, out blue))
            {
                alpha = 255;
                return true;
            }
        }

        // rgba(r, g, b, a)
        var rgbaMatch = RgbaPattern.Match(value);
        if (rgbaMatch.Success)
        {
            if (byte.TryParse(rgbaMatch.Groups[1].Value, out red) &&
                byte.TryParse(rgbaMatch.Groups[2].Value, out green) &&
                byte.TryParse(rgbaMatch.Groups[3].Value, out blue) &&
                decimal.TryParse(rgbaMatch.Groups[4].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var alphaValue) &&
                alphaValue >= 0m && alphaValue <= 1m)
            {
                // 非法透明度直接拒绝；有效值转换为八位通道。
                alpha = (byte)Math.Round(alphaValue * 255m);
                return true;
            }
        }

        return false;
    }

}
