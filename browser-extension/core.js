/* Shared validation only. Text and pairing codes are never logged. */
(function (root) {
  'use strict';
  const MAX_TEXT = 4000;
  const MAX_BATCH_ENTRIES = 4;
  const FALLBACK_LANGUAGES = [
    {name: '简体中文', code: 'zh-CN'}, {name: '繁體中文', code: 'zh-TW'},
    {name: 'English', code: 'en'}, {name: '日本語', code: 'ja'},
    {name: '한국어', code: 'ko'}, {name: 'Français', code: 'fr'},
    {name: 'Deutsch', code: 'de'}, {name: 'Español', code: 'es'},
    {name: 'Русский', code: 'ru'}
  ];
  function validLanguage(code, allowAuto) {
    return typeof code === 'string' && ((allowAuto && code === 'auto') || /^[a-z]{2,3}(?:-[A-Za-z]{2,8}){0,2}$/.test(code));
  }
  function validText(text) {
    return typeof text === 'string' && text.trim().length > 0 && text.length <= MAX_TEXT;
  }
  function languages(status) {
    const result = Array.isArray(status && status.languages)
      ? status.languages.filter(item => item && validLanguage(item.code, false) && typeof item.name === 'string' && item.name.length <= 80)
      : [];
    return result.length ? result : FALLBACK_LANGUAGES;
  }
  function direction(status) {
    return {
      sourceLanguage: validLanguage(status && status.sourceLanguage, true) ? status.sourceLanguage : 'auto',
      targetLanguage: validLanguage(status && status.targetLanguage, false) ? status.targetLanguage : 'zh-CN'
    };
  }
  function groups(records) {
    const result = [];
    let group = [], length = 0;
    for (const record of records) {
      if (!validText(record.text)) continue;
      if (group.length && (length + record.text.length > MAX_TEXT || group.length >= MAX_BATCH_ENTRIES)) {
        result.push(group); group = []; length = 0;
      }
      group.push(record); length += record.text.length;
    }
    if (group.length) result.push(group);
    return result;
  }
  root.BabelCore = Object.freeze({MAX_TEXT, MAX_BATCH_ENTRIES, validLanguage, validText, languages, direction, groups});
})(globalThis);
