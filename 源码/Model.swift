// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit
import Foundation

// MARK: - 数据模型

struct UsageRow {
    let label: String
    let percent: Int          // 已用百分比；可能 >100（超限），圆环自己夹到一圈
    let resetAt: Date?
    let priority: Int
}

final class PetModel {
    var rows: [UsageRow] = []
    var tier: String = ""
    var lastFetch: Date?
    var errorMsg: String?      // 有错误时置文案；成功后清空
    var asleep: Bool = false   // 拿不到数据时宠物睡觉
    var loading: Bool = true
    var needsLogin: Bool = false  // 没有可用令牌，等用户登录
    var sessions: [SessionActivity] = []
    var hovered: Bool = false     // 鼠标悬停时展开详情
    var detailsPinned: Bool = false  // 菜单里固定展开（不依赖鼠标）

    var anyBusy: Bool { sessions.contains { $0.busy } }

    /// 正在跑的 + 已完成但还没看的（未读会一直留着，直到点掉或该会话又开始工作）
    var visibleSessions: [SessionActivity] {
        Array(sessions.filter { $0.busy || $0.unread }.prefix(PetView.maxBlocks))
    }

    var maxPercent: Int { rows.map { $0.percent }.max() ?? 0 }

    /// 圆环用：外环=5小时，内环=每周（全部模型）；取不到就按顺序退化
    /// 圆环：外环=5小时；内环=用得最多的那条每周限额（可能是全部模型，
    /// 也可能是某个模型的专属周限额——后者更紧时必须显示它，否则会误导）
    var ringRows: (outer: UsageRow?, inner: UsageRow?) {
        let outer = rows.first { $0.label.contains("5 小时") } ?? rows.first
        let weeklies = rows.filter { $0.label.hasPrefix("每周") }
        let inner = weeklies.max { $0.percent < $1.percent }
            ?? rows.first { $0.label != outer?.label }
        return (outer, inner)
    }
}

