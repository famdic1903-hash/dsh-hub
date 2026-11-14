/**
 * @local/dsh-whale-pet — Client half.
 *
 * - Renders one small toggle button below the composer: "鲸鱼跳到桌面" / "鲸鱼回到窗口".
 * - Polls the Host state route so the button and the in-window whale stay in sync even
 *   when the pet window itself asks to come back (its right-click menu).
 * - While the pet is outside, hides the in-window whale widget (`.dshwv-root`) so the
 *   same fish never exists twice; the CSS rule is removed as soon as it comes back.
 */
window.__ModuleLoader__.load({
  id: '@local/dsh-whale-pet',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const API = '/api/whale-pet'
    const HIDE_CLASS = 'dsh-whale-pet-outside'
    const POLL_MS = 1200

    const state = {
      mode: 'inside',
      running: false,
      busy: false,
      error: '',
      listeners: new Set(),
    }

    let translate = null

    function emit() {
      for (const listener of state.listeners) {
        try {
          listener()
        } catch {
          // a broken listener must not stop the others
        }
      }
    }

    function patch(next) {
      Object.assign(state, next)
      emit()
    }

    function t(key, fallback) {
      if (translate === null) return fallback
      try {
        const value = translate(key)
        return typeof value === 'string' && value !== '' && value !== key ? value : fallback
      } catch {
        return fallback
      }
    }

    function applyMode(nextMode) {
      if (nextMode === 'outside' || nextMode === 'inside') state.mode = nextMode
    }

    function devicePixelRatio() {
      const value = typeof window.devicePixelRatio === 'number' ? window.devicePixelRatio : 1
      return Number.isFinite(value) && value >= 0.5 && value <= 4 ? Math.round(value * 100) / 100 : 1
    }

    /**
     * The desktop pet cannot read the whale widget's authenticated balance route, so this
     * page (a same-origin browser peer) fetches it and forwards the snapshot to the Host,
     * which serves it to the pet on click (bubble text).
     */
    async function pushBalance() {
      try {
        const balanceResponse = await fetch('/dsh-whale/balance.json', { cache: 'no-store' })
        if (!balanceResponse.ok) return
        const balance = await balanceResponse.json()
        if (balance === null || typeof balance !== 'object' || balance.ok === false) return
        if (!Number.isFinite(Number(balance.totalBalance))) return
        await fetch(`${API}/balance`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            totalBalance: balance.totalBalance,
            currency: balance.currency,
            todayUsage: balance.todayUsage,
            todayUsageCurrency: balance.todayUsageCurrency,
            // 峰谷（高峰/空闲）与法定节假日清单：桌宠气泡要和窗口内挂件显示一致
            isPeak: typeof balance.isPeak === 'boolean' ? balance.isPeak : undefined,
            peakNextChangeAt: Number.isFinite(Number(balance.peakNextChangeAt)) ? Number(balance.peakNextChangeAt) : undefined,
            peakHolidays: Array.isArray(balance.peakHolidays) ? balance.peakHolidays : undefined,
          }),
        })
      } catch {
        // the widget route or the Host may not be ready yet; the next poll retries
      }
    }

    async function readState() {
      try {
        const response = await fetch(`${API}/state?dpr=${devicePixelRatio()}`, {
          headers: { accept: 'application/json' },
          cache: 'no-store',
        })
        if (!response.ok) return
        const data = await response.json()
        if (data === null || typeof data !== 'object') return
        applyMode(data.mode)
        patch({ running: Boolean(data.running), error: data.error ? String(data.error) : '' })
      } catch {
        // Host not reachable yet: keep the last known state, the next poll retries.
      }
    }

    async function toggle() {
      if (state.busy) return
      const target = state.mode === 'outside' ? 'inside' : 'outside'
      patch({ busy: true, error: '' })
      try {
        const response = await fetch(`${API}/toggle`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ mode: target, dpr: devicePixelRatio() }),
        })
        const data = await response.json().catch(() => null)
        if (data !== null && typeof data === 'object') {
          applyMode(data.mode)
          patch({
            running: Boolean(data.running),
            error: data.error ? String(data.error) : '',
          })
        } else {
          patch({ error: '切换失败：DSH 主机没有返回结果' })
        }
      } catch {
        patch({ error: '切换失败：连不上 DSH 主机' })
      } finally {
        patch({ busy: false })
        emit()
        void readState()
      }
    }

    function WhalePetButton() {
      const [, force] = React.useReducer((value) => value + 1, 0)
      React.useEffect(() => {
        state.listeners.add(force)
        return () => {
          state.listeners.delete(force)
        }
      }, [])
      const outside = state.mode === 'outside'
      const label = outside ? t('toggle.back', '鲸鱼回到窗口') : t('toggle.out', '鲸鱼跳到桌面')
      const title = outside
        ? t('tip.back', '把鲸鱼收回 DSH 窗口（也可以右键桌面上的鲸鱼 → 回到 DSH 窗口）')
        : t('tip.out', '把「余额小鲸鱼」放到 DSH 窗口外的桌面上，可拖到任意显示器')
      return h(
        'button',
        {
          type: 'button',
          title,
          'aria-label': title,
          'data-whale-pet-toggle': outside ? 'outside' : 'inside',
          disabled: state.busy,
          onClick: () => {
            void toggle()
          },
          style: {
            display: 'inline-flex',
            alignItems: 'center',
            gap: '4px',
            height: '22px',
            padding: '0 10px',
            borderRadius: '999px',
            border: '1px solid rgba(128,128,128,0.32)',
            background: outside ? 'rgba(74,125,255,0.16)' : 'transparent',
            color: 'inherit',
            font: 'inherit',
            fontSize: '12px',
            lineHeight: '20px',
            cursor: state.busy ? 'progress' : 'pointer',
            opacity: state.busy ? 0.6 : 0.9,
            whiteSpace: 'nowrap',
          },
        },
        `${state.busy ? '…' : '🐳'} ${label}${state.error !== '' ? ' ⚠' : ''}`,
      )
    }

    return {
      inject: ['slots', 'locale'],
      apply(ctx) {
        // 1) Locale dictionaries (best effort; the button falls back to built-in Chinese).
        ctx.effect(
          () => {
            const disposers = []
            const register = (locale, dict) => {
              try {
                disposers.push(ctx.locale.register('whalePet', locale, dict))
              } catch {
                // locale service may not accept a dynamic namespace on some builds
              }
            }
            register('zh', { 'toggle.out': '鲸鱼跳到桌面', 'toggle.back': '鲸鱼回到窗口', 'tip.out': '把「余额小鲸鱼」放到 DSH 窗口外的桌面上，可拖到任意显示器', 'tip.back': '把鲸鱼收回 DSH 窗口（也可以右键桌面上的鲸鱼 → 回到 DSH 窗口）' })
            register('zh-CN', { 'toggle.out': '鲸鱼跳到桌面', 'toggle.back': '鲸鱼回到窗口', 'tip.out': '把「余额小鲸鱼」放到 DSH 窗口外的桌面上，可拖到任意显示器', 'tip.back': '把鲸鱼收回 DSH 窗口（也可以右键桌面上的鲸鱼 → 回到 DSH 窗口）' })
            register('en', { 'toggle.out': 'Whale to desktop', 'toggle.back': 'Whale back in window', 'tip.out': 'Move the balance whale onto the desktop, outside the DSH window. Drag it to any monitor.', 'tip.back': 'Bring the whale back into the DSH window (or right-click the whale on the desktop).' })
            try {
              translate = ctx.locale.bind('whalePet')
            } catch {
              translate = null
            }
            return () => {
              translate = null
              for (const dispose of disposers) {
                try {
                  dispose()
                } catch {
                  // ignore duplicate-dispose errors
                }
              }
            }
          },
          'whale-pet: client locale',
        )

        // 2) While the pet is out, hide the in-window whale so it never shows twice.
        ctx.effect(
          () => {
            const style = document.createElement('style')
            style.setAttribute('data-dsh-whale-pet', 'hide-in-window-whale')
            style.textContent = [
              `html.${HIDE_CLASS} .dshwv-root{opacity:0 !important;pointer-events:none !important}`,
              `html.${HIDE_CLASS} .dshwv-menu-btn{opacity:0 !important;pointer-events:none !important}`,
            ].join('\n')
            document.head.appendChild(style)
            return () => {
              style.remove()
              document.documentElement.classList.remove(HIDE_CLASS)
            }
          },
          'whale-pet: hide in-window whale style',
        )

        ctx.effect(() => {
          const sync = () => {
            document.documentElement.classList.toggle(HIDE_CLASS, state.mode === 'outside')
          }
          state.listeners.add(sync)
          sync()
          return () => {
            state.listeners.delete(sync)
            document.documentElement.classList.remove(HIDE_CLASS)
          }
        }, 'whale-pet: mode -> DOM sync')

        // 3) Poll the Host so a recall from the pet's own menu updates the page too.
        ctx.effect(() => {
          const timer = setInterval(() => {
            void readState()
          }, POLL_MS)
          void readState()
          return () => clearInterval(timer)
        }, 'whale-pet: state polling')

        // 4) Forward the live balance snapshot to the Host for the desktop pet's bubble.
        ctx.effect(() => {
          const timer = setInterval(() => {
            void pushBalance()
          }, 60_000)
          void pushBalance()
          return () => clearInterval(timer)
        }, 'whale-pet: balance relay')

        // 5) The toggle control, under the composer.
        ctx.slots.inject('conversation.composer.dock', () =>
          ctx.slots.register(
            {
              name: 'conversation.composer.dock',
              id: 'whale-pet-toggle',
              order: 20,
            },
            WhalePetButton,
          ),
        )
      },
    }
  },
})
