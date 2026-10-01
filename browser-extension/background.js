'use strict';
importScripts('core.js');
const BRIDGE = 'http://127.0.0.1:17863';
const pending = new Map();
// Pairing credentials stay in trusted extension contexts, never page DOM/content scripts.
chrome.storage.local.setAccessLevel({accessLevel: 'TRUSTED_CONTEXTS'}).catch(() => {});

function extensionContext(sender) {
  return sender.id === chrome.runtime.id && typeof sender.url === 'string' && sender.url.startsWith(chrome.runtime.getURL(''));
}
function trusted(sender) {
  if (sender.id !== chrome.runtime.id) return false;
  if (extensionContext(sender)) return true;
  if (sender.tab) return /^https?:\/\//.test(sender.url || sender.tab.url || '') && sender.frameId === 0;
  return false;
}
function key(sender, jobId) {
  if (typeof jobId !== 'string' || !/^[A-Za-z0-9_-]{1,80}$/.test(jobId)) throw new Error('无效任务。');
  return `${sender.tab ? sender.tab.id : 'extension'}:${jobId}`;
}
async function request(path, body, controller) {
  const {pairingToken} = await chrome.storage.local.get('pairingToken');
  if (typeof pairingToken !== 'string' || !/^[A-Za-z0-9_-]{24,160}$/.test(pairingToken)) {
    throw new Error('请先在扩展设置里粘贴巴别塔小窗口中的浏览器配对码。');
  }
  const abort = controller || new AbortController();
  let timedOut = false;
  // MV3 may terminate a worker waiting more than 30s for a fetch response.
  // Four independent translations at 25s each also keep a batch below 5min.
  const timer = setTimeout(() => { timedOut = true; abort.abort(); }, path === '/v1/translate' ? 25000 : 8000);
  try {
    const response = await fetch(BRIDGE + path, {
      method: body === undefined ? 'GET' : 'POST',
      headers: {'X-Babel-Token': pairingToken, ...(body === undefined ? {} : {'Content-Type': 'application/json'})},
      ...(body === undefined ? {} : {body: JSON.stringify(body)}),
      signal: abort.signal, redirect: 'error', credentials: 'omit', cache: 'no-store'
    });
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) throw new Error('配对码不匹配，或网页连接未开启。请检查巴别塔小窗口。');
      let detail = '';
      try { const error = await response.json(); detail = typeof error.error === 'string' ? error.error : ''; } catch (_) {}
      throw new Error(detail || `本地翻译未完成（${response.status}）。原文已保留。`);
    }
    const data = await response.json();
    if (!data || typeof data !== 'object') throw new Error('本地程序返回格式不正确。');
    return data;
  } catch (error) {
    if (error.name === 'AbortError') {
      if (timedOut && path === '/v1/translate') throw new Error('本地翻译等待超过 25 秒。请先在巴别塔预热模型，或减少文字后重试；原文和草稿已保留。');
      if (timedOut) throw new Error('本机连接等待超时，请检查巴别塔是否已启动。原文和草稿已保留。');
      throw new Error('翻译已停止，原文和草稿已保留。');
    }
    if (error instanceof TypeError) throw new Error('无法连接本机巴别塔。请启动新版并开启浏览器连接。');
    throw error;
  } finally { clearTimeout(timer); }
}
async function handle(message, sender) {
  if (!trusted(sender) || !message || typeof message.type !== 'string') throw new Error('拒绝不受支持的请求。');
  if (message.type === 'status') {
    const status = await request('/v1/status');
    const {inputButtonsEnabled = true} = await chrome.storage.local.get('inputButtonsEnabled');
    return {status, inputButtonsEnabled: inputButtonsEnabled !== false};
  }
  if (message.type === 'preferences') {
    const {inputButtonsEnabled = true} = await chrome.storage.local.get('inputButtonsEnabled');
    return {inputButtonsEnabled: inputButtonsEnabled !== false};
  }
  if (message.type === 'saveConnection') {
    if (!extensionContext(sender)) throw new Error('只能从扩展设置保存配对码。');
    const stored = await chrome.storage.local.get('pairingToken');
    const token = String(message.token || stored.pairingToken || '').trim();
    if (!/^[A-Za-z0-9_-]{24,160}$/.test(token)) throw new Error('请完整粘贴巴别塔显示的配对码。');
    await chrome.storage.local.set({pairingToken: token, inputButtonsEnabled: message.inputButtonsEnabled !== false});
    const tabs = await chrome.tabs.query({});
    for (const tab of tabs) chrome.tabs.sendMessage(tab.id, {type: 'preferencesChanged', inputButtonsEnabled: message.inputButtonsEnabled !== false}).catch(() => {});
    return {status: await request('/v1/status')};
  }
  if (message.type === 'settings') {
    if (!extensionContext(sender)) throw new Error('只能从巴别塔菜单修改方向。');
    if (!BabelCore.validLanguage(message.sourceLanguage, true) || !BabelCore.validLanguage(message.targetLanguage, false) || message.sourceLanguage === message.targetLanguage) {
      throw new Error('请选择不同的原文和译文语言。');
    }
    return {status: await request('/v1/settings', {sourceLanguage: message.sourceLanguage, targetLanguage: message.targetLanguage})};
  }
  if (message.type === 'cancel') {
    const jobKey = key(sender, message.jobId);
    const active = pending.get(jobKey);
    if (active) active.abort();
    return {};
  }
  if (message.type === 'translateBatch') {
    if (!sender.tab || extensionContext(sender)) throw new Error('翻译请求必须来自网页。');
    const jobKey = key(sender, message.jobId);
    const texts = message.texts;
    if (!Array.isArray(texts) || texts.length < 1 || texts.length > BabelCore.MAX_BATCH_ENTRIES || texts.some(text => !BabelCore.validText(text)) || texts.reduce((total, text) => total + text.length, 0) > BabelCore.MAX_TEXT) {
      throw new Error('每批翻译最多 4 条、不超过 4000 字。');
    }
    const controller = new AbortController();
    if (pending.has(jobKey)) throw new Error('这个翻译任务正在进行。');
    pending.set(jobKey, controller);
    try {
      const status = await request('/v1/status', undefined, controller);
      const current = BabelCore.direction(status);
      const sourceLanguage = message.sourceLanguage === undefined ? current.sourceLanguage : message.sourceLanguage;
      const targetLanguage = message.targetLanguage === undefined ? current.targetLanguage : message.targetLanguage;
      const allowed = BabelCore.languages(status).map(item => item.code);
      if (!(sourceLanguage === 'auto' || allowed.includes(sourceLanguage)) || !allowed.includes(targetLanguage) || sourceLanguage === targetLanguage) throw new Error('当前翻译方向不可用，请在小窗口重新选择。');
      const translations = [];
      // Independent requests preserve text-node boundaries: the model cannot merge/reorder HTML parts.
      for (const text of texts) {
        if (controller.signal.aborted) throw new Error('翻译已停止。');
        const result = await request('/v1/translate', {text, sourceLanguage, targetLanguage}, controller);
        if (typeof result.text !== 'string' || !result.text.trim() || result.text.length > 20000) throw new Error('翻译没有返回有效文字，原文已保留。');
        translations.push(result.text);
      }
      return {translations, sourceLanguage, targetLanguage};
    } finally { pending.delete(jobKey); }
  }
  throw new Error('不支持这个操作。');
}
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  handle(message, sender).then(result => sendResponse({ok: true, ...result}), error => sendResponse({ok: false, error: error.message || '操作未完成，原文已保留。'}));
  return true;
});
chrome.tabs.onRemoved.addListener(tabId => {
  for (const [jobKey, controller] of pending) if (jobKey.startsWith(`${tabId}:`)) { controller.abort(); pending.delete(jobKey); }
});
