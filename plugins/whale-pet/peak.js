/**
 * 峰谷时段判定（与「余额小鲸鱼」挂件同一套规则）。
 *
 * 北京时间（UTC+8）周一至周五 9:00–12:00、14:00–18:00 为高峰；
 * 其余时段——包括周末（2026-08-23 起）与中国法定节假日全天（2026-09-19 起）——为空闲。
 * 法定节假日清单无法在桌宠侧写死（每年更新），由页面从挂件 `/dsh-whale/balance.json`
 * 中继过来；本模块只负责「按清单 + 时段规则」判定。
 */

export const PEAK_HOURS = [
  [9, 12],
  [14, 18],
]

// 北京时间 2026-08-23 00:00：这一刻起周末全天按谷价。
export const WEEKEND_VALLEY_FROM_SEC = Math.floor(Date.UTC(2026, 7, 22, 16, 0, 0) / 1000)
// 北京时间 2026-09-19 00:00：这一刻起法定节假日（含调休放假的工作日）全天按谷价。
export const HOLIDAY_VALLEY_FROM_SEC = Math.floor(Date.UTC(2026, 8, 18, 16, 0, 0) / 1000)

/**
 * @param {number} timeSec epoch 秒
 * @param {Set<string>|undefined} holidays 北京时间 `YYYY-MM-DD` 的法定节假日集合
 * @returns {boolean} true = 高峰时段
 */
export function isPeakTime(timeSec, holidays) {
  const seconds = Number(timeSec)
  if (!Number.isFinite(seconds)) return false
  // 加 8 小时后再按 UTC 读取，得到北京时间日历。
  const beijing = new Date(seconds * 1000 + 8 * 3600 * 1000)
  if (seconds >= WEEKEND_VALLEY_FROM_SEC) {
    const dow = beijing.getUTCDay()
    if (dow === 0 || dow === 6) return false
  }
  if (seconds >= HOLIDAY_VALLEY_FROM_SEC && holidays !== undefined && holidays.has(beijing.toISOString().slice(0, 10))) {
    return false
  }
  const hour = beijing.getUTCHours()
  for (const [start, end] of PEAK_HOURS) {
    if (hour >= start && hour < end) return true
  }
  return false
}

/**
 * @param {number} timeSec epoch 秒
 * @param {Set<string>|undefined} holidays
 * @returns {'高峰时段'|'空闲时段'}
 */
export function peakStatusText(timeSec, holidays) {
  return isPeakTime(timeSec, holidays) ? '高峰时段' : '空闲时段'
}
