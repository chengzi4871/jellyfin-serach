(function () {
    'use strict';
    if (window.__jellyfinVisualSearchLoaded) return;
    window.__jellyfinVisualSearchLoaded = true;

    var API = window.JellyfinVisualSearch = window.JellyfinVisualSearch || {};
    if (API.initialized) return;
    API.search = function (query, libraryIds, limit) {
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('VisualSearch/Search'), data: JSON.stringify({ query: query, libraryIds: libraryIds || [], limit: limit || 30 }), contentType: 'application/json', dataType: 'json' });
    };
    API.health = function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/Health')); };
    API.initialized = true;

    var launcherId = 'jf-visual-search-launcher';
    var panelId = 'jf-visual-search-panel';
    var observerTimer = 0;

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) { return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]; });
    }

    function prop(object, name) {
        if (!object) return undefined;
        var lower = name.charAt(0).toLowerCase() + name.slice(1);
        return object[name] != null ? object[name] : object[lower];
    }

    async function errorMessage(error) {
        if (error && typeof error.text === 'function' && typeof error.status === 'number') {
            var raw = '';
            try { raw = await error.text(); } catch (_) { }
            var body = null;
            try { body = raw ? JSON.parse(raw) : null; } catch (_) { }
            var detail = prop(body, 'Message') || prop(body, 'Error') || raw || error.statusText || '请求失败';
            return 'HTTP ' + error.status + '：' + detail;
        }
        var legacyBody = error && (error.responseJSON || (error.response && error.response.data) || error.data);
        return prop(legacyBody, 'Message') || prop(legacyBody, 'Error') || (error && error.message) || String(error);
    }

    function ensureStyle() {
        if (document.getElementById('jf-visual-search-style')) return;
        var style = document.createElement('style');
        style.id = 'jf-visual-search-style';
        style.textContent = '#jf-visual-search-launcher{position:fixed;right:max(16px,env(safe-area-inset-right));bottom:max(18px,env(safe-area-inset-bottom));z-index:10000;border:0;border-radius:999px;background:var(--theme-primary-color,#00a4dc);color:#fff;width:48px;height:48px;font-size:22px;box-shadow:0 3px 14px #0008;cursor:pointer}#jf-visual-search-panel{position:fixed;z-index:10001;right:max(16px,env(safe-area-inset-right));top:max(64px,env(safe-area-inset-top));width:min(560px,calc(100vw - 32px));max-height:calc(100vh - 96px);overflow:auto;background:var(--theme-background,#202020);color:var(--theme-text-color,#fff);padding:16px;border-radius:10px;box-shadow:0 5px 30px #000b;box-sizing:border-box}#jf-visual-search-panel textarea{width:100%;min-height:70px;box-sizing:border-box;resize:vertical;background:var(--theme-background-accent,#333);color:inherit;border:1px solid #888;border-radius:4px;padding:8px}#jf-visual-search-panel button{min-height:38px;padding:6px 14px;margin:8px 6px 8px 0;cursor:pointer}#jf-visual-search-panel .jf-vs-result{display:block;padding:10px 0;border-top:1px solid #ffffff26;text-decoration:none;color:inherit}@media(max-width:600px){#jf-visual-search-panel{right:8px;left:8px;top:max(8px,env(safe-area-inset-top));width:auto;max-height:calc(100vh - 72px);padding:12px}#jf-visual-search-launcher{width:44px;height:44px;font-size:20px}}';
        document.head.appendChild(style);
    }

    function closePanel() { var panel = document.getElementById(panelId); if (panel) panel.remove(); }

    function renderResults(container, data) {
        var results = prop(data, 'Results');
        results = Array.isArray(results) ? results : [];
        if (!results.length) { container.innerHTML = '<p>没有找到结果。</p>'; return; }
        container.innerHTML = results.map(function (item) {
            var itemId = prop(item, 'ItemId');
            var detail = '#!/details?id=' + encodeURIComponent(itemId || '');
            var scores = '综合 ' + Number(prop(item, 'Score') || 0).toFixed(3);
            if (prop(item, 'VisualScore') != null) scores += ' · 画面 ' + Number(prop(item, 'VisualScore')).toFixed(3);
            if (prop(item, 'TitleScore') != null) scores += ' · 标题 ' + Number(prop(item, 'TitleScore')).toFixed(3);
            return '<a class="jf-vs-result" href="' + detail + '"><strong>' + escapeHtml(prop(item, 'Title') || itemId) + '</strong><br><small>' + escapeHtml(scores) + '</small></a>';
        }).join('');
    }

    function openPanel() {
        ensureStyle();
        var old = document.getElementById(panelId);
        if (old) { old.querySelector('textarea')?.focus(); return; }
        var panel = document.createElement('section');
        panel.id = panelId; panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-label', '视频语义搜索');
        panel.innerHTML = '<div style="display:flex;justify-content:space-between;align-items:center"><h3 style="margin:0">视频语义搜索</h3><button type="button" data-jf-vs-close aria-label="关闭">×</button></div><p style="margin:.6em 0">输入场景、人物、服装或颜色，例如：海边、红色衣服、室内两个人。</p><textarea data-jf-vs-query placeholder="输入搜索内容"></textarea><br><button type="button" data-jf-vs-search>搜索</button><span data-jf-vs-status></span><div data-jf-vs-results></div>';
        document.body.appendChild(panel);
        var query = panel.querySelector('[data-jf-vs-query]');
        var status = panel.querySelector('[data-jf-vs-status]');
        panel.querySelector('[data-jf-vs-close]').onclick = closePanel;
        var run = async function () {
            var value = String(query.value || '').trim();
            if (!value) { status.textContent = '请输入搜索内容'; query.focus(); return; }
            var button = panel.querySelector('[data-jf-vs-search]'); button.disabled = true; status.textContent = ' 搜索中…';
            try { var data = await API.search(value, [], 30); var results = prop(data, 'Results') || []; status.textContent = ' 共 ' + (Array.isArray(results) ? results.length : 0) + ' 个结果'; renderResults(panel.querySelector('[data-jf-vs-results]'), data); }
            catch (e) { status.textContent = ' 搜索失败：' + await errorMessage(e); }
            finally { button.disabled = false; }
        };
        panel.querySelector('[data-jf-vs-search]').onclick = run;
        query.addEventListener('keydown', function (e) { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') run(); });
        query.focus();
    }

    function ensureLauncher() {
        if (!document.body || document.getElementById(launcherId)) return;
        ensureStyle();
        var button = document.createElement('button'); button.id = launcherId; button.type = 'button'; button.title = '视频语义搜索'; button.setAttribute('aria-label', '视频语义搜索'); button.textContent = '🧠'; button.onclick = openPanel;
        document.body.appendChild(button);
    }

    function scheduleEnsure() { if (observerTimer) return; observerTimer = window.setTimeout(function () { observerTimer = 0; ensureLauncher(); }, 120); }
    ensureLauncher();
    new MutationObserver(scheduleEnsure).observe(document.body, { childList: true, subtree: true });
})();
