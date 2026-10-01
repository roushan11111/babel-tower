(function () {
  'use strict';
  if (globalThis.__babelTowerContentLoaded) return;
  globalThis.__babelTowerContentLoaded = true;
  const originalNodes = new Map();
  const drafts = new WeakMap();
  const SKIP = 'script,style,noscript,template,code,pre,kbd,samp,svg,math,canvas,form,input,textarea,select,option,button,nav,[contenteditable],[role="textbox"],[role="combobox"],[translate="no"],[data-babel-protected],[data-babel-tower]';
  const SENSITIVE = /pass(?:word|code)?|passwd|secret|token|api.?key|one.?time|\botp\b|verification|credit|card.?number|cvv|cvc|security.?code|验证码|校验码|密码|口令|密钥|银行卡|身份证/iu;
  let pageJob = null, focused = null, inputJob = null, inputEnabled = true, composing = false;
  let positionFrame = 0, toastTimer = 0;

  async function send(message) {
    let response;
    try { response = await chrome.runtime.sendMessage(message); }
    catch (_) { throw new Error('扩展已更新或连接已断开，请刷新网页。'); }
    if (!response || !response.ok) throw new Error(response && response.error || '操作未完成，原文已保留。');
    return response;
  }
  function id(prefix) { return `${prefix}_${crypto.randomUUID().replaceAll('-', '')}`; }
  function visible(element) {
    if (!element || !element.isConnected || element.closest('[hidden],[inert],[aria-hidden="true"]')) return false;
    let current = element;
    while (current && current.nodeType === Node.ELEMENT_NODE) {
      const style = getComputedStyle(current);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) return false;
      current = current.parentElement;
    }
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }
  function editableRoot(target) {
    if (!(target instanceof Element)) return null;
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) return target;
    let element = target;
    if (!element.isContentEditable) return null;
    while (element.parentElement && element.parentElement.isContentEditable) element = element.parentElement;
    return element;
  }
  function eligible(element) {
    if (!visible(element) || element.matches(':disabled') || element.hasAttribute('readonly') || element.closest('[aria-readonly="true"],[aria-disabled="true"]')) return false;
    if (element instanceof HTMLInputElement && !['text', 'search'].includes(element.type)) return false;
    if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) && !element.isContentEditable) return false;
    const metadata = ['name', 'id', 'autocomplete', 'inputmode', 'aria-label', 'placeholder', 'data-babel-protected'].map(name => element.getAttribute(name) || '').join(' ');
    if (element.hasAttribute('data-babel-protected') || SENSITIVE.test(metadata) || /(?:^|\s)(?:numeric|decimal|tel|email|url)(?:\s|$)/i.test(metadata)) return false;
    if (element.form && element.form.querySelector('input[type="password"],input[autocomplete="one-time-code"]')) return false;
    // Replacing non-text objects or locked subtrees would destroy editor content.
    if (element.isContentEditable && element.querySelector('img,video,audio,canvas,iframe,table,[contenteditable="false"],[data-babel-protected]')) return false;
    return true;
  }
  function draftText(element) {
    return element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement ? element.value : element.innerText;
  }
  function unchangedDraft(element, original, revision) {
    const active = document.activeElement;
    return element.isConnected && eligible(element) && (drafts.get(element) || 0) === revision &&
      draftText(element) === original && focused === element && !composing &&
      (active === element || element.isContentEditable && element.contains(active));
  }

  // Shadow DOM contains the UI only. No pairing code, source text or translation is exposed here.
  const host = document.createElement('div');
  host.setAttribute('data-babel-tower', '');
  host.style.cssText = 'all:initial;position:fixed;inset:0;z-index:2147483647;pointer-events:none;';
  const shadow = host.attachShadow({mode: 'closed'});
  const style = document.createElement('style');
  style.textContent = ':host{all:initial}button{box-sizing:border-box;font:600 13px system-ui,"Microsoft YaHei",sans-serif;border:1px solid #528fe8;background:#fff;color:#155abb;border-radius:8px;box-shadow:0 2px 8px #0002;cursor:pointer;width:29px;height:29px;padding:0;pointer-events:auto}button:hover{background:#edf5ff}button:disabled{color:#667;background:#eee;cursor:wait}#translate{position:fixed;display:none}#notice{display:none;position:fixed;right:14px;bottom:18px;max-width:330px;font:13px/1.5 system-ui,"Microsoft YaHei",sans-serif;color:#fff;background:#223349;box-shadow:0 2px 14px #0003;padding:10px 14px;border-radius:8px;pointer-events:none;white-space:pre-wrap}';
  const button = document.createElement('button');
  button.id = 'translate'; button.type = 'button'; button.textContent = '译';
  button.title = '将这个输入框中的整句翻译为巴别塔当前目标语言，保留未发送状态';
  button.setAttribute('aria-label', '翻译当前输入框');
  const notice = document.createElement('div'); notice.id = 'notice'; notice.setAttribute('role', 'status');
  shadow.append(style, button, notice); document.documentElement.append(host);
  function toast(message) {
    notice.textContent = message; notice.style.display = 'block';
    clearTimeout(toastTimer); toastTimer = setTimeout(() => { notice.style.display = 'none'; }, 5500);
  }
  function positionButton() {
    positionFrame = 0;
    if (!inputEnabled || !focused || !eligible(focused) || composing || document.activeElement !== focused && !focused.contains(document.activeElement)) {
      button.style.display = 'none'; return;
    }
    const rect = focused.getBoundingClientRect();
    if (rect.bottom <= 0 || rect.top >= innerHeight || rect.right <= 0 || rect.left >= innerWidth) { button.style.display = 'none'; return; }
    button.style.left = `${Math.max(4, Math.min(innerWidth - 33, rect.right + 5))}px`;
    button.style.top = `${Math.max(4, Math.min(innerHeight - 33, rect.top + Math.min(12, Math.max(0, (rect.height - 29) / 2))))}px`;
    button.style.display = 'block';
  }
  function schedulePosition() { if (!positionFrame) positionFrame = requestAnimationFrame(positionButton); }
  button.addEventListener('pointerdown', event => { event.preventDefault(); event.stopPropagation(); });
  button.addEventListener('click', async event => {
    event.preventDefault(); event.stopPropagation();
    const element = focused;
    if (inputJob || composing || !element || !eligible(element)) return;
    // Read only the explicitly clicked field, never nearby draft fields.
    const original = draftText(element), revision = drafts.get(element) || 0;
    if (!BabelCore.validText(original)) { toast('请先输入一句文字，每次最多 4000 字。'); return; }
    inputJob = {id: id('draft'), element}; button.disabled = true; button.textContent = '…';
    try {
      const result = await send({type: 'translateBatch', jobId: inputJob.id, texts: [original]});
      if (!unchangedDraft(element, original, revision)) {
        toast('输入内容或焦点已变化，这次译文未写入。'); return;
      }
      const translated = result.translations[0];
      if (element.maxLength >= 0 && translated.length > element.maxLength) { toast('译文超出输入框字数限制，原文已保留。'); return; }
      const before = new InputEvent('beforeinput', {bubbles: true, cancelable: true, inputType: 'insertReplacementText', data: translated});
      if (!element.dispatchEvent(before)) { toast('这个编辑器未允许替换，原文已保留。'); return; }
      // A synchronous beforeinput handler can move focus, start an IME composition,
      // or mutate/revert a draft. Recheck all authorization conditions before writing.
      if (!unchangedDraft(element, original, revision)) { toast('输入内容、焦点或输入法状态已变化，原文已保留。'); return; }
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
        const prototype = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(prototype, 'value').set;
        // Invoke the native setter rather than a React-overridden instance setter.
        setter.call(element, translated);
        element.dispatchEvent(new InputEvent('input', {bubbles: true, inputType: 'insertReplacementText', data: translated}));
        try { element.setSelectionRange(translated.length, translated.length); } catch (_) {}
      } else {
        const selection = getSelection(), range = document.createRange();
        range.selectNodeContents(element); selection.removeAllRanges(); selection.addRange(range);
        // Browser editing command preserves undo and supplies editor input events.
        // No HTML fallback: unsupported rich editors retain their original draft.
        if (!unchangedDraft(element, original, revision)) { toast('输入内容、焦点或输入法状态已变化，原文已保留。'); return; }
        if (!document.execCommand('insertText', false, translated)) { toast('这个编辑器不支持文字替换，原文已保留。'); return; }
      }
      toast(draftText(element) === translated ? '译文已放入输入框，尚未发送。' : '这个编辑器未接受译文，请检查输入框。');
    } catch (error) { toast(error.message); }
    finally { inputJob = null; button.disabled = false; button.textContent = '译'; schedulePosition(); }
  });
  document.addEventListener('focusin', event => { focused = editableRoot(event.target); composing = false; schedulePosition(); }, true);
  document.addEventListener('focusout', schedulePosition, true);
  document.addEventListener('input', event => { const element = editableRoot(event.target); if (element) drafts.set(element, (drafts.get(element) || 0) + 1); schedulePosition(); }, true);
  document.addEventListener('compositionstart', () => { composing = true; schedulePosition(); }, true);
  document.addEventListener('compositionend', () => { composing = false; schedulePosition(); }, true);
  document.addEventListener('scroll', schedulePosition, true);
  window.addEventListener('resize', schedulePosition);
  new MutationObserver(schedulePosition).observe(document.documentElement, {subtree: true, childList: true, attributes: true, attributeFilter: ['style', 'class', 'hidden', 'disabled', 'readonly', 'contenteditable', 'aria-hidden', 'aria-disabled']});
  send({type: 'preferences'}).then(result => { inputEnabled = result.inputButtonsEnabled; focused = editableRoot(document.activeElement); schedulePosition(); }).catch(() => {});

  function pageState() {
    return {running: Boolean(pageJob && !pageJob.finished), total: pageJob ? pageJob.total : 0, done: pageJob ? pageJob.done : 0, skipped: pageJob ? pageJob.skipped : 0, changed: originalNodes.size, message: pageJob ? pageJob.message : ''};
  }
  function candidates(targetLanguage) {
    const result = []; let characters = 0, skipped = 0;
    for (const node of originalNodes.keys()) if (!node.isConnected) originalNodes.delete(node);
    const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) {
      const parent = node.parentElement;
      if (!parent || parent.closest(SKIP) || !visible(parent) || originalNodes.has(node)) continue;
      const original = node.nodeValue, text = original.trim();
      if (!/[\p{L}]/u.test(text)) continue;
      const languageElement = parent.closest('[lang]');
      if (languageElement && !['HTML', 'BODY'].includes(languageElement.tagName)) {
        const language = languageElement.lang.toLowerCase();
        if (language === targetLanguage.toLowerCase() || !targetLanguage.startsWith('zh-') && language.split('-')[0] === targetLanguage.toLowerCase()) continue;
      }
      if (text.length > BabelCore.MAX_TEXT || result.length >= 600 || characters + text.length > 120000) { skipped++; continue; }
      characters += text.length;
      result.push({node, original, text, leading: original.match(/^\s*/)[0], trailing: original.match(/\s*$/)[0]});
    }
    return {records: result, skipped};
  }
  function stopPage() {
    if (!pageJob || pageJob.finished) return;
    pageJob.cancelled = true;
    send({type: 'cancel', jobId: pageJob.id}).catch(() => {});
  }
  function restorePage() {
    stopPage(); let restored = 0, changed = 0;
    for (const [node, record] of originalNodes) {
      if (node.isConnected && node.nodeValue === record.translation) { node.nodeValue = record.original; restored++; }
      else if (node.isConnected) changed++;
    }
    originalNodes.clear();
    toast(changed ? `已还原 ${restored} 处；${changed} 处网页内容已变化，保留网页现状。` : `已还原 ${restored} 处原文。`);
    return {restored, changed};
  }
  async function translatePage(job, sourceLanguage, targetLanguage) {
    try {
      const snapshot = candidates(targetLanguage);
      job.total = snapshot.records.length; job.skipped = snapshot.skipped;
      if (!job.total) { job.message = '没有需要翻译的可见网页文字。'; return; }
      for (const batch of BabelCore.groups(snapshot.records)) {
        if (job.cancelled) break;
        const current = batch.filter(record => record.node.isConnected && record.node.nodeValue === record.original);
        job.skipped += batch.length - current.length;
        if (!current.length) continue;
        const result = await send({type: 'translateBatch', jobId: job.id, texts: current.map(record => record.text), sourceLanguage, targetLanguage});
        if (job.cancelled) break;
        if (!Array.isArray(result.translations) || result.translations.length !== current.length) throw new Error('译文格式不正确，未替换的原文已保留。');
        current.forEach((record, index) => {
          if (!record.node.isConnected || record.node.nodeValue !== record.original || !visible(record.node.parentElement) || record.node.parentElement.closest(SKIP)) { job.skipped++; return; }
          const translation = record.leading + result.translations[index] + record.trailing;
          originalNodes.set(record.node, {original: record.original, translation});
          record.node.nodeValue = translation; job.done++;
        });
      }
      job.message = job.cancelled ? '已停止，已完成部分可还原。' : `已翻译 ${job.done} 处${job.skipped ? `，跳过 ${job.skipped} 处` : ''}。`;
    } catch (error) { job.message = error.message; }
    finally { job.finished = true; toast(job.message); }
  }
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'preferencesChanged') { inputEnabled = message.inputButtonsEnabled !== false; schedulePosition(); sendResponse({ok: true}); }
    if (message.type === 'pageState') sendResponse({ok: true, state: pageState()});
    if (message.type === 'stopPage') { stopPage(); sendResponse({ok: true, state: pageState()}); }
    if (message.type === 'restorePage') sendResponse({ok: true, ...restorePage(), state: pageState()});
    if (message.type === 'translatePage') {
      if (pageJob && !pageJob.finished) { sendResponse({ok: false, error: '网页正在翻译，可以先停止。'}); return; }
      if (!BabelCore.validLanguage(message.sourceLanguage, true) || !BabelCore.validLanguage(message.targetLanguage, false)) { sendResponse({ok: false, error: '翻译方向无效。'}); return; }
      pageJob = {id: id('page'), total: 0, done: 0, skipped: 0, message: '正在本地翻译…', cancelled: false, finished: false};
      translatePage(pageJob, message.sourceLanguage, message.targetLanguage);
      sendResponse({ok: true, state: pageState()});
    }
  });
})();
