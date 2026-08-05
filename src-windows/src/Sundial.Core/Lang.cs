// Interface language. Mirrors src/Lang.swift on the macOS side.
//
// The strings are written inline as bilingual pairs rather than pulled from a resource file: there
// are only two languages and about a hundred strings, and keeping the English in the source means
// the code still reads as English while you are working on it.

using System.Globalization;

namespace Sundial.Core;

public enum Lang { En, Zh }

public static class Language
{
    /// <summary>Read by every <see cref="L"/> call, so it is deliberately a plain static rather than
    /// something threaded through the drawing code. Written once at launch and again when the menu
    /// changes it, both on the UI thread.</summary>
    public static Lang Current { get; set; } = SystemDefault;

    /// <summary>What to use when the user has not chosen. Following the system is the only sensible
    /// default: a Chinese user should not have to find a menu before the app is readable.</summary>
    public static Lang SystemDefault =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? Lang.Zh : Lang.En;

    /// <summary>A bilingual literal. <c>L("Thinking", "正在思考")</c>.</summary>
    public static string L(string en, string zh) => Current == Lang.Zh ? zh : en;

    /// <summary>
    /// <b>Row.Label is the key, this is the text.</b>
    /// <para>PetModel.RingRows decides which allowance goes on which dial by matching Label against
    /// "5 hours" and the "Weekly" prefix, and WeeklyShortName strips that prefix again. Translating
    /// Label itself would leave both dials permanently empty — which is exactly what happened the
    /// first time these strings were changed. So the label stays English for ever, and only this
    /// is ever drawn.</para>
    /// </summary>
    public static string DisplayLabel(string label)
    {
        if (Current != Lang.Zh) return label;
        if (label == "5 hours") return "5 小时";
        if (label == "Weekly · all models") return "每周 · 全部模型";
        const string prefix = "Weekly · ";
        if (label.StartsWith(prefix, StringComparison.Ordinal))
            return "每周 · " + label[prefix.Length..];   // the model name is not translated
        return label;
    }
}
