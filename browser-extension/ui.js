(function () {
  'use strict';
  const $ = id => document.getElementById(id);
  let connected = false, dirty = false, tabId = null, pollTimer = 0, busy = false;
  async function send(message) {
    const result = await chrome.runtime.sendMessage(message);
    if (!result || !result.ok) throw new Error(result && result.error || '操作未完成。');
    return result;
  }
  function showMessage(text, error = false) { $('message').textContent = text; $('message').classList.toggle('error', error); }
  function showError(error) { showMessage(error.message || String(error), true); }
  function options(select, items, selected) {
    select.replaceChildren();
    for (const item of items) { const option = document.createElement('option'); option.value = item.code; option.textContent = item.name; select.append(option); }
    select.value = selected;
  }
  function populate(status) {
    const list = BabelCore.languages(status), direction = BabelCore.direction(status);
    options($('source'), [{name: '自动识别', code: 'auto'}, ...list], direction.sourceLanguage);
    options($('target'), list, direction.targetLanguage);
    connected = true; dirty = false;
    $('connection').textContent = '已连接本机巴别塔';
    $('source').disabled = false; $('target').disabled = false;
    $('saveDirection').disabled = true;
    if ($('translatePage')) $('translatePage').disabled = busy;
  }
  async function refreshConnection() {
    try {
      const result = await send({type: 'status'}); populate(result.status);
      if ($('inputButtonsEnabled')) $('inputButtonsEnabled').checked = result.inputButtonsEnabled;
    } catch (error) { connected = false; $('connection').textContent = error.message; }
  }
  function changed() {
    dirty = true; $('saveDirection').disabled = !connected;
    if ($('translatePage')) $('translatePage').disabled = true;
    showMessage('点击“保存翻译方向”后生效。');
  }
  $('source').addEventListener('change', changed); $('target').addEventListener('change', changed);
  $('saveDirection').addEventListener('click', async () => {
    $('saveDirection').disabled = true; showMessage('');
    try { const result = await send({type: 'settings', sourceLanguage: $('source').value, targetLanguage: $('target').value}); populate(result.status); showMessage('已保存，小窗口和输入框“译”同步使用这个方向。'); }
    catch (error) { showError(error); $('saveDirection').disabled = !connected; }
  });
  if ($('saveConnection')) $('saveConnection').addEventListener('click', async () => {
    $('saveConnection').disabled = true; showMessage('');
    try {
      const result = await send({type: 'saveConnection', token: $('pairingToken').value, inputButtonsEnabled: $('inputButtonsEnabled').checked});
      $('pairingToken').value = ''; populate(result.status); showMessage('连接设置已保存。');
    } catch (error) {
      connected = false; $('connection').textContent = error.message;
      $('source').disabled = true; $('target').disabled = true; $('saveDirection').disabled = true;
      showError(error);
    }
    finally { $('saveConnection').disabled = false; }
  });
  async function pageMessage(message) {
    if (tabId === null) {
      const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
      if (!tab || !Number.isInteger(tab.id)) throw new Error('请先打开一个普通网页。');
      tabId = tab.id;
    }
    let result;
    try { result = await chrome.tabs.sendMessage(tabId, message); }
    catch (_) { throw new Error('这个页面暂不支持，或扩展刚加载。请刷新普通网页后重试。'); }
    if (!result || !result.ok) throw new Error(result && result.error || '网页操作未完成。');
    return result;
  }
  function state(result) {
    const value = result.state;
    busy = value.running;
    $('translatePage').disabled = !connected || dirty || busy;
    $('stopPage').disabled = !busy;
    $('pageState').textContent = busy ? `正在本地翻译：${value.done}/${value.total} 处。关闭菜单后仍继续。` : value.message || '网页正文一键翻译；输入框草稿请点旁边的“译”。';
    clearTimeout(pollTimer);
    if (busy) pollTimer = setTimeout(() => pageMessage({type: 'pageState'}).then(state).catch(showError), 1000);
  }
  if ($('translatePage')) {
    $('translatePage').addEventListener('click', async () => {
      showMessage(''); $('translatePage').disabled = true;
      try {
        // Snapshot the saved app direction at the moment the user requests a page translation.
        const status = await send({type: 'status'}); populate(status.status);
        state(await pageMessage({type: 'translatePage', ...BabelCore.direction(status.status)}));
      } catch (error) { showError(error); $('translatePage').disabled = !connected || dirty || busy; }
    });
    $('restorePage').addEventListener('click', () => pageMessage({type: 'restorePage'}).then(state).catch(showError));
    $('stopPage').addEventListener('click', () => pageMessage({type: 'stopPage'}).then(state).catch(showError));
    $('openOptions').addEventListener('click', () => chrome.runtime.openOptionsPage());
    pageMessage({type: 'pageState'}).then(state).catch(() => {});
  }
  refreshConnection();
  window.addEventListener('unload', () => clearTimeout(pollTimer));
})();
