// Interface language.
//
// The strings are written inline as bilingual pairs rather than pulled from a .strings bundle:
// there are only two languages and about a hundred strings, and keeping the English in the source
// means the code still reads as English while you are working on it. A key-based table would put
// every message one lookup away from the site that shows it.

import Foundation

enum Lang: String {
    case en, zh

    /// What to use when the user has not chosen. Following the system is the only sensible default:
    /// a Chinese user should not have to find a menu before the app is readable.
    static var systemDefault: Lang {
        let pref = Locale.preferredLanguages.first ?? "en"
        return pref.hasPrefix("zh") ? .zh : .en
    }
}

/// Read by every `L(...)` call, so it is deliberately a plain global rather than something to be
/// threaded through the drawing code. Written once at launch and again when the menu changes it,
/// both on the main thread.
var appLang: Lang = .systemDefault

/// A bilingual literal. `L("Thinking", "正在思考")`.
func L(_ en: String, _ zh: String) -> String { appLang == .zh ? zh : en }

extension UsageRow {
    /// **`label` is the key, this is the text.**
    ///
    /// `PetModel.ringRows` decides which allowance goes on which dial by matching `label` against
    /// "5 hours" and the "Weekly" prefix, and `weeklyShortName` strips that prefix again. Translating
    /// `label` itself would leave both dials permanently empty — which is exactly what happened the
    /// first time these strings were changed. So the label stays English for ever, and only this
    /// property is ever drawn.
    var displayLabel: String {
        guard appLang == .zh else { return label }
        if label == "5 hours" { return "5 小时" }
        if label == "Weekly · all models" { return "每周 · 全部模型" }
        if label.hasPrefix("Weekly · ") {
            return "每周 · " + label.dropFirst("Weekly · ".count)   // the model name is not translated
        }
        return label
    }
}
