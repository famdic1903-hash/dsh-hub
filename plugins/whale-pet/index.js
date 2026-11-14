/**
 * @local/dsh-whale-pet — Host half.
 *
 * Owns the "跳出窗口" desktop pet:
 *   - the desired mode (`inside` / `outside`) persisted under $DSH_HOME/.whale-pet/,
 *   - a few loopback HTTP control routes used by the Client half and by the pet window,
 *   - the native pet process lifecycle (spawn WhalePet.exe, kill it on recall, stop on dispose).
 *
 * The pet itself is a separate C# Win32 layered window (pet/WhalePet.exe) because a Web
 * page can never draw outside its own Electron window.
 */
import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { isPeakTime } from './peak.js'

export const inject = ['webServer']

const PLUGIN_DIR = dirname(fileURLToPath(import.meta.url))
const PET_EXE = join(PLUGIN_DIR, 'pet', 'WhalePet.exe')

const DSH_HOME = (() => {
  const fromEnv = typeof process.env.DSH_HOME === 'string' ? process.env.DSH_HOME.trim() : ''
  return fromEnv !== '' ? fromEnv : join(homedir(), '.dsh')
})()

/** Test/automation hook: keep the plugin's own state out of $DSH_HOME when asked to. */
const STATE_DIR = (() => {
  const override = typeof process.env.DSH_WHALE_PET_STATE_DIR === 'string' ? process.env.DSH_WHALE_PET_STATE_DIR.trim() : ''
  return override !== '' ? override : join(DSH_HOME, '.whale-pet')
})()
const STATE_FILE = join(STATE_DIR, 'state.json')
const POS_FILE = join(STATE_DIR, 'pos.json')
const RUN_FILE = join(STATE_DIR, 'run.json')
const BALANCE_FILE = join(STATE_DIR, 'balance.json')

const API = '/api/whale-pet'
/** Pet window side in DIP (logical pixels); the pet scales it by the reported device pixel ratio. */
const DEFAULT_SIZE = 300
const PET_IDLE_BOB = true

/** Structured but non-fatal logging so the Host log tells the pet story. */
function log(...args) {
  console.log('[whale-pet]', ...args)
}

function warn(...args) {
  console.warn('[whale-pet]', ...args)
}

async function readJson(path) {
  try {
    return JSON.parse(await readFile(path, 'utf8'))
  } catch {
    return undefined
  }
}

async function writeJson(path, value) {
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, 'utf8')
}

function clampSize(value, fallback) {
  const size = Number(value)
  if (!Number.isFinite(size)) return fallback
  return Math.min(520, Math.max(120, Math.round(size)))
}

/**
 * The pet renders in physical pixels; the browser reports DIP. Keep the scale factor so the
 * pet can convert `size` (and its own default placement) instead of trusting a hard-coded DPI.
 */
function parseDpr(value) {
  const dpr = Number(value)
  if (!Number.isFinite(dpr) || dpr < 0.5 || dpr > 4) return undefined
  return Math.round(dpr * 100) / 100
}

/**
 * Pet geometry is persisted by the Host (the pet can be denied file writes when it is
 * spawned from the Host process), so accept only a complete, sane physical-pixel record.
 */
function readGeometry(value) {
  if (value === null || typeof value !== 'object') return undefined
  const x = Number(value.x)
  const y = Number(value.y)
  const size = Number(value.size)
  if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(size)) return undefined
  if (Math.abs(x) > 200000 || Math.abs(y) > 200000) return undefined
  if (size < 48 || size > 1024) return undefined
  return { x: Math.round(x), y: Math.round(y), size: Math.round(size) }
}

function isLoopback(req) {
  const address = typeof req.socket?.remoteAddress === 'string' ? req.socket.remoteAddress : ''
  return address === '127.0.0.1' || address === '::1' || address === '::ffff:127.0.0.1'
}

function sendJson(res, status, value) {
  if (res.writableEnded) return
  res.statusCode = status
  res.setHeader('content-type', 'application/json; charset=utf-8')
  res.setHeader('cache-control', 'no-store')
  res.end(JSON.stringify(value))
}

function rejectMethod(res, allowed) {
  res.setHeader('allow', allowed)
  sendJson(res, 405, { ok: false, error: 'method-not-allowed' })
}

async function readJsonBody(req, limit = 16 * 1024) {
  const chunks = []
  let total = 0
  for await (const chunk of req) {
    total += chunk.length
    if (total > limit) throw new Error('payload-too-large')
    chunks.push(chunk)
  }
  if (total === 0) return undefined
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch {
    return undefined
  }
}

/** Locate the installed `dsh-whale-widget` package that owns the in-window whale. */
async function findWhaleWidgetDir() {
  const candidates = []
  const profilesRoot = join(DSH_HOME, 'profiles')
  try {
    const names = await readdir(profilesRoot, { withFileTypes: true })
    for (const entry of names) {
      if (!entry.isDirectory()) continue
      candidates.push(join(profilesRoot, entry.name, 'node_modules'))
    }
  } catch {
    // no profiles directory: fall through to the env-provided profile
  }
  const profileDir = typeof process.env.DSH_PROFILE_DIR === 'string' ? process.env.DSH_PROFILE_DIR.trim() : ''
  if (profileDir !== '') candidates.push(join(profileDir, 'node_modules'))
  for (const nodeModules of candidates) {
    const dir = join(nodeModules, 'dsh-whale-widget')
    if (existsSync(dir)) return dir
  }
  return undefined
}

/** Best-effort location of the whale widget's default character artwork. */
async function defaultCharacterImage() {
  const widgetDir = await findWhaleWidgetDir()
  if (widgetDir === undefined) return undefined
  for (const file of ['DSniang1.png', 'DSniang02.png']) {
    const path = join(widgetDir, 'assets', file)
    if (existsSync(path)) return path
  }
  return undefined
}

async function resolveImage(config) {
  const configured = typeof config.imagePath === 'string' ? config.imagePath.trim() : ''
  if (configured !== '' && existsSync(configured)) return configured
  const fallback = await defaultCharacterImage()
  if (fallback !== undefined) return fallback
  return undefined
}

/** The whale widget's built-in press/release sound presets (soundSet id -> asset file names). */
const SOUND_PRESETS = {
  duck: ['Ya1.mp3', 'Ya2.mp3'],
  fx1: ['D1.mp3', 'D2.mp3'],
}

function currencySymbol(code) {
  const normalized = String(code ?? 'CNY').trim().toUpperCase()
  if (normalized === 'CNY' || normalized === 'RMB') return '¥'
  if (normalized === 'USD') return '$'
  return normalized === '' ? '¥' : `${normalized} `
}

function formatMoney(value, currency) {
  const amount = Number(value)
  if (!Number.isFinite(amount)) return '—'
  return `${currencySymbol(currency)}${amount.toFixed(2)}`
}

/**
 * 峰谷时段（与挂件同源）：
 * - 首选页面中继来的挂件服务端值（`isPeak` + `peakNextChangeAt`，10 分钟内有效且未跨切换点）；
 * - 否则用同一套北京时间规则本地判定（节假日清单同样来自页面中继，缺失时按普通工作日算）。
 */
const HOLIDAY_RE = /^\d{4}-\d{2}-\d{2}$/
let holidayValley = new Set()

function setHolidayValley(list) {
  if (!Array.isArray(list)) return
  const next = new Set()
  for (const item of list.slice(0, 400)) {
    const text = String(item ?? '').trim()
    if (HOLIDAY_RE.test(text)) next.add(text)
  }
  holidayValley = next
}

function currentPeak(nowSec) {
  const snapshot = balanceSnapshot
  const at = Number(snapshot.at ?? 0)
  const age = Date.now() - at
  const next = Number(snapshot.peakNextChangeAt)
  if (typeof snapshot.isPeak === 'boolean' && Number.isFinite(at) && age >= 0 && age < 10 * 60 * 1000) {
    if (!Number.isFinite(next) || nowSec < next) return snapshot.isPeak === true
  }
  return isPeakTime(nowSec, holidayValley)
}

function peakLine(nowSec) {
  return `峰谷 ${currentPeak(nowSec) ? '高峰时段' : '空闲时段'}`
}

/** Resolve the widget's configured press/release sounds from its own assets directory. */
async function resolvePokeSounds(widgetDir, settings) {
  if (widgetDir === undefined) return {}
  if (settings !== null && typeof settings === 'object' && settings.sound === false) return {}
  const preset = SOUND_PRESETS[String(settings?.soundSet ?? 'duck')] ?? SOUND_PRESETS.duck
  const assets = join(widgetDir, 'assets')
  const result = {}
  try {
    const press = join(assets, preset[0])
    const release = join(assets, preset[1])
    if (existsSync(press)) result.pressSound = press
    if (existsSync(release)) result.releaseSound = release
  } catch {
    // a broken preset must never break the click
  }
  return result
}

/**
 * Last balance snapshot: pushed by the Client half (the page can read `/dsh-whale/balance.json`
 * with the browser's credentials, the pet and the Host cannot), with the widget's own ledger
 * file as a cold-start fallback so a click right after boot still shows a number.
 */
let balanceSnapshot = {}

async function loadBalanceSnapshot() {
  const saved = await readJson(BALANCE_FILE)
  if (saved !== null && typeof saved === 'object' && Number.isFinite(Number(saved.totalBalance))) balanceSnapshot = saved
  if (saved !== null && typeof saved === 'object') setHolidayValley(saved.peakHolidays)
}

async function ledgerBalance() {
  const usage = await readJson(join(DSH_HOME, '.dshw-usage.json'))
  if (usage === null || typeof usage !== 'object') return undefined
  const total = Number(usage.lastBalance)
  if (!Number.isFinite(total)) return undefined
  return {
    totalBalance: total,
    currency: 'CNY',
    todayUsage: Number.isFinite(Number(usage.todayUsage)) ? Number(usage.todayUsage) : undefined,
    todayUsageCurrency: 'CNY',
    at: 0,
    source: 'ledger',
  }
}

async function currentBalance() {
  const age = Date.now() - Number(balanceSnapshot.at ?? 0)
  if (Number.isFinite(Number(balanceSnapshot.totalBalance)) && age >= 0 && age < 10 * 60 * 1000) return balanceSnapshot
  return await ledgerBalance()
}

export function apply(ctx, config = {}) {
  const settings = config !== null && typeof config === 'object' ? config : {}
  const desiredSize = clampSize(settings.size, DEFAULT_SIZE)

  /** @type {{ mode: 'inside' | 'outside', dpr: number, child: import('node:child_process').ChildProcess | undefined, stopping: boolean, external: boolean, lastError: string | undefined }} */
  const runtime = { mode: 'inside', dpr: 1, child: undefined, stopping: false, external: false, lastError: undefined }

  async function persistState() {
    await writeJson(STATE_FILE, { mode: runtime.mode, dpr: runtime.dpr, updatedAt: new Date().toISOString() })
  }

  async function apiBase() {
    try {
      const origin = new URL(ctx.webServer.getOrigin())
      // The pet always talks to the loopback listener; keep the bound port, drop any LAN host.
      return `${origin.protocol}//127.0.0.1:${origin.port}`
    } catch {
      const fromEnv = typeof process.env.DSH_WEB_URL === 'string' ? process.env.DSH_WEB_URL.trim() : ''
      return fromEnv !== '' ? fromEnv.replace(/\/+$/, '') : 'http://127.0.0.1:59391'
    }
  }

  function isPetAlive() {
    return runtime.child !== undefined && runtime.child.exitCode === null && runtime.child.killed === false
  }

  async function startPet() {
    if (isPetAlive()) return
    if (!existsSync(PET_EXE)) {
      throw new Error(`桌面宠物程序不存在：${PET_EXE}`)
    }
    const imagePath = await resolveImage(settings)
    if (imagePath === undefined) {
      throw new Error('找不到鲸鱼角色图（余额小鲸鱼挂件未安装？）')
    }
    // The pet renders in physical pixels on a system-DPI-aware canvas and auto-detects the
    // display scale; the Host only supplies the DIP default and replays the last geometry.
    const savedGeometry = await readJson(POS_FILE)
    const runConfig = {
      imagePath,
      posPath: POS_FILE,
      apiBase: await apiBase(),
      size: desiredSize,
      idleBob: PET_IDLE_BOB,
    }
    const physical = readGeometry(savedGeometry)
    if (physical !== undefined) runConfig.physical = physical
    await writeJson(RUN_FILE, runConfig)

    const child = spawn(PET_EXE, [RUN_FILE], {
      cwd: PLUGIN_DIR,
      windowsHide: true,
      stdio: 'ignore',
    })
    runtime.child = child
    runtime.stopping = false
    runtime.external = false
    runtime.lastError = undefined
    const startedAt = Date.now()
    child.once('error', (error) => {
      runtime.lastError = error instanceof Error ? error.message : String(error)
      warn('failed to start pet:', runtime.lastError)
      runtime.child = undefined
    })
    child.once('exit', (code) => {
      runtime.child = undefined
      if (runtime.stopping) {
        runtime.stopping = false
        return
      }
      if (Date.now() - startedAt < 2000 && code === 0) {
        // The single-instance mutex refused a second window: an orphan pet is still running.
        runtime.external = true
        runtime.lastError = undefined
        log('another pet instance is already running; keeping mode=outside')
        return
      }
      runtime.external = false
      log(`pet exited (code ${String(code)}); restoring in-window mode`)
      runtime.mode = 'inside'
      void persistState().catch((error) => warn('persistState failed:', error))
    })
    log(`pet started: ${PET_EXE} size=${runConfig.size}`)
  }

  async function stopPet() {
    runtime.external = false
    const child = runtime.child
    if (child === undefined) return
    runtime.stopping = true
    try {
      child.kill()
    } catch (error) {
      warn('failed to stop pet:', error instanceof Error ? error.message : String(error))
    }
    runtime.child = undefined
  }

  async function setMode(nextMode) {
    const target = nextMode === 'outside' ? 'outside' : 'inside'
    if (target === 'outside') {
      await startPet()
      runtime.mode = 'outside'
    } else {
      await stopPet()
      runtime.mode = 'inside'
    }
    await persistState()
    return runtime.mode
  }

  async function stateView() {
    return {
      ok: true,
      mode: runtime.mode,
      running: isPetAlive() || runtime.external,
      size: desiredSize,
      dpr: runtime.dpr,
      geometry: readGeometry(await readJson(POS_FILE)),
      error: runtime.lastError,
    }
  }

  /** Adopt the browser-reported device pixel ratio so pet geometry matches the page's DIP. */
  function adoptDpr(value) {
    const reported = parseDpr(value)
    if (reported === undefined || reported === runtime.dpr) return
    runtime.dpr = reported
    void persistState().catch((error) => warn('persistState failed:', error))
  }

  function guard(req, res) {
    if (!isLoopback(req)) {
      sendJson(res, 403, { ok: false, error: 'loopback-only' })
      return false
    }
    return true
  }

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/state`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'GET') return rejectMethod(res, 'GET')
          try {
            adoptDpr(new URL(req.url ?? '/', 'http://x').searchParams.get('dpr'))
          } catch {
            // malformed URL: answer with the current view anyway
          }
          sendJson(res, 200, await stateView())
        },
      }),
    `whale-pet: GET ${API}/state`,
  )

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/toggle`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'POST') return rejectMethod(res, 'POST')
          const body = await readJsonBody(req)
          adoptDpr(body?.dpr)
          const requested = body?.mode === 'inside' || body?.mode === 'outside' ? body.mode : runtime.mode === 'outside' ? 'inside' : 'outside'
          try {
            await setMode(requested)
            sendJson(res, 200, await stateView())
          } catch (error) {
            runtime.mode = 'inside'
            runtime.lastError = error instanceof Error ? error.message : String(error)
            warn('toggle failed:', runtime.lastError)
            void persistState().catch((error) => warn('persistState failed:', error))
            sendJson(res, 500, { ...(await stateView()), ok: false })
          }
        },
      }),
    `whale-pet: POST ${API}/toggle`,
  )

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/recall`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'POST') return rejectMethod(res, 'POST')
          await setMode('inside')
          sendJson(res, 200, await stateView())
        },
      }),
    `whale-pet: POST ${API}/recall`,
  )

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/pos`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'POST') return rejectMethod(res, 'POST')
          const geometry = readGeometry(await readJsonBody(req))
          if (geometry === undefined) {
            sendJson(res, 400, { ok: false, error: 'invalid-geometry' })
            return
          }
          // The pet may be denied file writes; the Host persists geometry for the next launch.
          await writeJson(POS_FILE, { ...geometry, updatedAt: new Date().toISOString() })
          sendJson(res, 200, { ok: true, geometry })
        },
      }),
    `whale-pet: POST ${API}/pos`,
  )

  // The desktop pet's "poke": click → bounce + sounds + balance bubble. The pet cannot read
  // `/dsh-whale/*` (browser credentials) nor the widget's internals, so the Host assembles it.
  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/poke`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'GET') return rejectMethod(res, 'GET')
          try {
            const widgetDir = await findWhaleWidgetDir()
            const widgetSettings = (await readJson(join(DSH_HOME, '.dshw-size.json'))) ?? {}
            const balance = await currentBalance()
            const lines = []
            if (balance !== undefined) {
              lines.push(`余额 ${formatMoney(balance.totalBalance, balance.currency)}`)
              if (Number.isFinite(Number(balance.todayUsage))) {
                lines.push(`今日已用 ${formatMoney(balance.todayUsage, balance.todayUsageCurrency ?? balance.currency)}`)
              }
            } else {
              lines.push('余额 —')
            }
            // 与窗口内挂件首次点击的气泡对齐：余额 / 今日已用 / 峰谷时段
            lines.push(peakLine(Math.floor(Date.now() / 1000)))
            const sounds = await resolvePokeSounds(widgetDir, widgetSettings)
            const rawVolume = Number(widgetSettings.vol)
            const volume = Number.isFinite(rawVolume) ? Math.min(1, Math.max(0, rawVolume)) : 0.9
            sendJson(res, 200, {
              ok: true,
              lines,
              volume,
              bubbleMs: 5000,
              ...sounds,
            })
          } catch (error) {
            warn('poke failed:', error instanceof Error ? error.message : String(error))
            sendJson(res, 200, { ok: true, lines: ['余额 —'], volume: 0.9, bubbleMs: 5000 })
          }
        },
      }),
    `whale-pet: GET ${API}/poke`,
  )

  // The page can read the widget's authenticated balance route; it pushes the snapshot here.
  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/balance`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'POST') return rejectMethod(res, 'POST')
          const body = await readJsonBody(req)
          const totalBalance = Number(body?.totalBalance)
          if (!Number.isFinite(totalBalance)) {
            sendJson(res, 400, { ok: false, error: 'invalid-balance' })
            return
          }
          const snapshot = {
            totalBalance,
            currency: typeof body.currency === 'string' && body.currency !== '' ? body.currency : 'CNY',
            todayUsage: Number.isFinite(Number(body.todayUsage)) ? Number(body.todayUsage) : undefined,
            todayUsageCurrency:
              typeof body.todayUsageCurrency === 'string' && body.todayUsageCurrency !== ''
                ? body.todayUsageCurrency
                : undefined,
            // 峰谷：挂件服务端值 + 节假日清单（页面从挂件接口中继，挂件是单一来源）
            isPeak: typeof body.isPeak === 'boolean' ? body.isPeak : undefined,
            peakNextChangeAt: Number.isFinite(Number(body.peakNextChangeAt)) ? Number(body.peakNextChangeAt) : undefined,
            peakHolidays: Array.isArray(body.peakHolidays)
              ? body.peakHolidays.filter((item) => HOLIDAY_RE.test(String(item ?? '').trim())).slice(0, 400)
              : undefined,
            at: Date.now(),
            source: 'page',
          }
          setHolidayValley(snapshot.peakHolidays)
          balanceSnapshot = snapshot
          await writeJson(BALANCE_FILE, snapshot).catch((error) => warn('balance write failed:', error))
          sendJson(res, 200, { ok: true })
        },
      }),
    `whale-pet: POST ${API}/balance`,
  )

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: `${API}/ping`,
        handler: async (req, res) => {
          if (!guard(req, res)) return
          if (req.method !== 'GET') return rejectMethod(res, 'GET')
          sendJson(res, 200, { ok: true, app: 'dsh-whale-pet' })
        },
      }),
    `whale-pet: GET ${API}/ping`,
  )

  ctx.effect(() => {
    const boot = async () => {
      await loadBalanceSnapshot()
      const saved = await readJson(STATE_FILE)
      const savedDpr = parseDpr(saved?.dpr)
      if (savedDpr !== undefined) runtime.dpr = savedDpr
      if (saved?.mode !== 'outside') {
        runtime.mode = 'inside'
        return
      }
      runtime.mode = 'outside'
      try {
        await startPet()
      } catch (error) {
        runtime.mode = 'inside'
        runtime.lastError = error instanceof Error ? error.message : String(error)
        warn('resume failed:', runtime.lastError)
        await persistState()
      }
    }
    void boot()
    return () => {
      void stopPet()
    }
  }, 'whale-pet: lifecycle')
}
