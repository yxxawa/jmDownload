(() => {
  'use strict';
  let pendingDialog = null;
  function confirm(message, label = '删除') {
    if (pendingDialog) return Promise.resolve(false);
    const previous = document.activeElement;
    const dialog = document.createElement('dialog');
    dialog.className = 'bulk-dialog';
    dialog.setAttribute('aria-label', '确认操作');
    dialog.innerHTML = '<form method="dialog"><h2>确认操作</h2><p></p><div><button value="cancel" autofocus>取消</button><button value="confirm" class="bulk-danger"></button></div></form>';
    dialog.querySelector('p').textContent = message;
    dialog.querySelector('.bulk-danger').textContent = label;
    document.body.append(dialog);
    pendingDialog = dialog;
    return new Promise(resolve => {
      const onKey = event => {
        if (event.key === 'Escape') {
          event.preventDefault(); event.stopImmediatePropagation(); dialog.close('cancel');
        }
      };
      window.addEventListener('keydown', onKey, true);
      dialog.addEventListener('close', () => {
        window.removeEventListener('keydown', onKey, true);
        pendingDialog = null;
        const accepted = dialog.returnValue === 'confirm';
        dialog.remove();
        if (previous?.isConnected) previous.focus({ preventScroll: true });
        resolve(accepted);
      }, { once: true });
      dialog.showModal();
    });
  }
  function create(config) {
    const list = document.querySelector(config.list);
    const toolbar = document.createElement('div');
    toolbar.className = 'bulk-toolbar';
    toolbar.dataset.bulkFor = list.id;
    toolbar.innerHTML = '<button type="button" data-bulk-manage><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m4 7 2 2 4-4M13 7h7M4 17l2 2 4-4M13 17h7"/></svg>管理</button><div class="bulk-controls" hidden><button type="button" data-bulk-all>全选</button><span class="bulk-count" aria-live="polite"></span><button type="button" data-bulk-delete class="bulk-danger">删除选中</button><button type="button" data-bulk-done>完成</button></div>';
    list.before(toolbar);
    const selected = new Set();
    let items = [], keys = new Map(), active = false, busy = false, scope = '';
    const controls = toolbar.querySelector('.bulk-controls');
    const manage = toolbar.querySelector('[data-bulk-manage]');
    const all = toolbar.querySelector('[data-bulk-all]');
    const remove = toolbar.querySelector('[data-bulk-delete]');
    const keyOf = item => String(config.key ? config.key(item) : item.id);
    function paint() {
      toolbar.hidden = keys.size === 0;
      manage.hidden = active;
      controls.hidden = !active;
      list.classList.toggle('bulk-managing', active);
      toolbar.classList.toggle('is-managing', active);
      toolbar.querySelector('.bulk-count').textContent = '已选 ' + selected.size + ' 项';
      all.textContent = selected.size === keys.size && keys.size ? '取消全选' : '全选';
      all.classList.toggle('is-all-selected', selected.size === keys.size && keys.size > 0);
      remove.textContent = config.removeLabel?.() || '删除选中';
      toolbar.querySelectorAll('button').forEach(button => { button.disabled = busy; });
      remove.disabled = busy || selected.size === 0;
      list.querySelectorAll(config.row).forEach((row, index) => {
        const item = items[index];
        const key = item == null ? '' : keyOf(item);
        const eligible = keys.has(key);
        row.dataset.bulkKey = eligible ? key : '';
        row.classList.toggle('bulk-row', active && eligible);
        row.classList.toggle('bulk-selected', active && selected.has(key));
        let picker = row.querySelector(':scope > .bulk-picker');
        if (!active || !eligible) { picker?.remove(); return; }
        if (!picker) {
          picker = document.createElement('label'); picker.className = 'bulk-picker';
          picker.innerHTML = '<input type="checkbox" class="bulk-choice"><svg class="bulk-check" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10"/><path d="m7.5 12 3 3 6-6"/></svg>';
          row.prepend(picker);
        }
        const choice = picker.querySelector('.bulk-choice');
        choice.checked = selected.has(key);
        choice.disabled = busy;
        choice.setAttribute('aria-label', '选择 ' + (config.title ? config.title(item) : item.title || item.id || '此项'));
      });
    }
    function refresh(nextItems, nextScope = scope) {
      if (nextScope !== scope) { active = false; selected.clear(); scope = nextScope; }
      items = nextItems;
      keys = new Map(items.filter(item => !config.eligible || config.eligible(item)).map(item => [keyOf(item), item]));
      for (const key of selected) if (!keys.has(key)) selected.delete(key);
      if (!keys.size) active = false;
      paint();
    }
    manage.onclick = () => { active = true; paint(); };
    toolbar.querySelector('[data-bulk-done]').onclick = () => { active = false; selected.clear(); paint(); };
    all.onclick = () => {
      if (selected.size === keys.size) selected.clear();
      else keys.forEach((_, key) => selected.add(key));
      paint();
    };
    list.addEventListener('click', event => {
      if (!active) return;
      const row = event.target.closest(config.row);
      if (!row || !list.contains(row) || !keys.has(row.dataset.bulkKey)) return;
      if (!event.target.classList.contains('bulk-choice')) event.preventDefault();
      event.stopImmediatePropagation();
      if (busy) return;
      const key = row.dataset.bulkKey;
      if (selected.has(key)) selected.delete(key); else selected.add(key);
      paint();
    }, true);
    remove.onclick = async () => {
      if (busy || !selected.size) return;
      const chosen = [...selected].map(key => keys.get(key));
      busy = true; paint();
      try {
        const accepted = await confirm(config.message(chosen.length), config.confirmLabel?.() || '删除');
        if (!accepted) return;
        await config.remove(chosen);
        chosen.forEach(item => selected.delete(keyOf(item)));
      } catch (error) { config.error(error); }
      finally { busy = false; paint(); }
    };
    paint();
    return { refresh, get active() { return active; } };
  }
  window.JMBulk = { create, confirm, closeDialog() { if (!pendingDialog) return false; pendingDialog.close('cancel'); return true; } };
})();
