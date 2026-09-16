/* Browser-local rendering preferences. No server settings or permissions are changed. */
(() => {
  'use strict';
  const defaults = { compatibleTiles: true, stars: true, pauseBackground: true, followSelf: false, effects: true };
  const prefix = location.pathname.startsWith('/servermap') ? '/servermap' : '';
  const storageKey = `servermap-web-settings:${location.host}${prefix}`;
  let saved;
  try { saved = JSON.parse(localStorage.getItem(storageKey)); } catch {}
  const values = Object.fromEntries(Object.entries(defaults).map(([key, value]) => [key, typeof saved?.[key] === 'boolean' ? saved[key] : value]));
  const listeners = new Set();
  const copy = {
    zh: {
      title: '网页设置', hint: '即时生效，仅保存在当前浏览器。', close: '关闭', reset: '恢复默认', storageError: '浏览器无法保存设置；本次页面仍然生效。',
      compatibleTiles: ['兼容瓦片合成', '使用普通颜色合成。地图空白或发白时建议开启；关闭后使用加法混合减轻瓦片接缝。'],
      stars: ['动态星空', '关闭星空绘制可减少持续的耗电和显卡占用。'],
      pauseBackground: ['后台暂停刷新', '离开此标签页时暂停常规地图更新，返回时补齐。权限和可见性变化仍会处理。'],
      followSelf: ['跟随自己', '登录且角色位置可见时保持居中；手动拖动地图会关闭跟随，可在此重新开启。'],
      effects: ['毛玻璃与阴影', '关闭界面模糊和装饰阴影以减轻绘制负担；移动端仍使用原有简化样式。']
    },
    en: {
      title: 'Web settings', hint: 'Applied immediately. Saved only in this browser.', close: 'Close', reset: 'Restore defaults', storageError: 'Settings cannot be saved in this browser; they still apply to this page.',
      compatibleTiles: ['Compatible tile compositing', 'Use normal compositing for blank or washed-out maps. Turning this off uses additive blending to reduce tile seams.'],
      stars: ['Animated stars', 'Turn off star drawing to reduce power and graphics usage.'],
      pauseBackground: ['Pause background updates', 'Pause routine map updates in a hidden tab and catch up on return. Permission and visibility changes are still handled.'],
      followSelf: ['Follow myself', 'Keep your visible character centered while signed in. Dragging the map turns following off; enable it here again.'],
      effects: ['Blur and shadows', 'Disable interface blur and decorative shadows to reduce rendering work. Mobile keeps its simplified styles.']
    }
  };
  function apply() {
    document.documentElement.classList.toggle('web-compatible-tiles', values.compatibleTiles);
    document.documentElement.classList.toggle('web-no-effects', !values.effects);
  }
  function set(key, value) {
    if (!(key in defaults) || typeof value !== 'boolean') return;
    values[key] = value;
    let persisted = true;
    try { localStorage.setItem(storageKey, JSON.stringify(values)); } catch { persisted = false; }
    apply();
    for (const listener of listeners) listener(key, persisted);
  }
  apply();
  window.ServerMapWebSettings = {
    values, set,
    paused: () => values.pauseBackground && document.hidden,
    create({ getLanguage, onChange, closePanels }) {
      const node = (tag, props = {}) => Object.assign(document.createElement(tag), props);
      const button = node('button', { id: 'webSettingsButton', type: 'button' });
      button.setAttribute('aria-haspopup', 'dialog');button.setAttribute('aria-controls', 'webSettingsDialog');
      // Tabler Icons: settings (MIT).
      button.innerHTML = '<svg class="icon icon-tabler icon-tabler-settings" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M10.325 4.317c.426 -1.756 2.924 -1.756 3.35 0a1.724 1.724 0 0 0 2.573 1.066c1.543 -.94 3.31 .826 2.37 2.37a1.724 1.724 0 0 0 1.065 2.572c1.756 .426 1.756 2.924 0 3.35a1.724 1.724 0 0 0 -1.066 2.573c.94 1.543 -.826 3.31 -2.37 2.37a1.724 1.724 0 0 0 -2.572 1.065c-.426 1.756 -2.924 1.756 -3.35 0a1.724 1.724 0 0 0 -2.573 -1.066c-1.543 .94 -3.31 -.826 -2.37 -2.37a1.724 1.724 0 0 0 -1.065 -2.572c-1.756 -.426 -1.756 -2.924 0 -3.35a1.724 1.724 0 0 0 1.066 -2.573c-.94 -1.543 .826 -3.31 2.37 -2.37c1 .608 2.296 .07 2.572 -1.065"/><path d="M9 12a3 3 0 1 0 6 0a3 3 0 0 0 -6 0"/></svg>';
      document.getElementById('languageButton').after(button);
      const dialog = node('dialog', { id: 'webSettingsDialog', className: 'map-dialog' });
      const header = node('header'), title = node('h2', { id: 'webSettingsTitle' }), close = node('button', { type: 'button', className: 'dialog-close', textContent: '×' });
      dialog.setAttribute('aria-labelledby', title.id);header.append(title, close);
      const body = node('div', { className: 'map-dialog-body' }), hint = node('p'), status = node('p');
      status.setAttribute('role', 'status');body.append(hint);
      const inputs = {};
      for (const key of Object.keys(defaults)) {
        const row = node('label', { className: 'web-setting' }), input = node('input', { type: 'checkbox', checked: values[key], id: `web-setting-${key}` });
        const label = node('span'), help = node('small', { id: `${input.id}-help` });
        input.setAttribute('aria-describedby', help.id);input.onchange = () => set(key, input.checked);
        row.append(input, label, help);body.append(row);inputs[key] = { input, label, help };
      }
      const footer = node('div', { className: 'web-settings-actions' }), reset = node('button', { type: 'button' }), done = node('button', { type: 'button' });
      footer.append(reset, done);body.append(status, footer);dialog.append(header, body);document.body.append(dialog);
      let storageFailed = false;
      function translate() {
        const text = copy[getLanguage()] || copy.en;
        button.title = text.title;button.setAttribute('aria-label', text.title);title.textContent = text.title;
        close.setAttribute('aria-label', text.close);done.textContent = text.close;reset.textContent = text.reset;hint.textContent = text.hint;
        status.textContent = storageFailed ? text.storageError : '';
        for (const [key, row] of Object.entries(inputs)) { row.label.textContent = text[key][0];row.help.textContent = text[key][1];row.input.checked = values[key]; }
      }
      listeners.add((key, persisted) => { storageFailed = !persisted;translate();onChange(key); });
      button.onclick = () => { closePanels();translate();dialog.showModal(); };
      close.onclick = done.onclick = () => dialog.close();
      reset.onclick = () => { for (const [key, value] of Object.entries(defaults)) set(key, value); };
      const outside = event => { const r = dialog.getBoundingClientRect();return event.target === dialog && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom); };
      let backdropDown = false;
      dialog.addEventListener('pointerdown', event => { backdropDown = outside(event); });
      dialog.addEventListener('click', event => { if (backdropDown && outside(event)) dialog.close();backdropDown = false; });
      dialog.addEventListener('keydown', event => event.stopPropagation());
      translate();return { translate };
    }
  };
})();
