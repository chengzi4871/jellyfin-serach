(function () {
    'use strict';
    if (window.__jellyfinVisualSearchLoaded) return;
    window.__jellyfinVisualSearchLoaded = true;

    var API = window.JellyfinVisualSearch = window.JellyfinVisualSearch || {};
    var launcherId = 'jf-visual-search-launcher';
    var panelId = 'jf-visual-search-panel';
    var styleId = 'jf-visual-search-style';
    var observerTimer = 0;

    function prop(object, name) {
        if (!object) return undefined;
        var lower = name.charAt(0).toLowerCase() + name.slice(1);
        return object[name] != null ? object[name] : object[lower];
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (character) {
            return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character];
        });
    }

    function clampScore(value) {
        var number = Number(value);
        return isFinite(number) ? Math.max(0, Math.min(1, number)) : 0;
    }

    function scoreText(value) {
        return (clampScore(value) * 100).toFixed(1) + '%';
    }

    function currentLibraryIds() {
        // Jellyfin library pages expose the collection folder in topParentId. On
        // other pages we deliberately leave the filter empty and search globally.
        var hash = String(window.location.hash || '');
        var queryIndex = hash.indexOf('?');
        if (queryIndex < 0) return [];
        var params = new URLSearchParams(hash.slice(queryIndex + 1));
        var id = params.get('topParentId');
        return id && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(id) ? [id] : [];
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

    API.search = function (query, libraryIds, limit) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('VisualSearch/Search'),
            data: JSON.stringify({ query: query, libraryIds: libraryIds || [], limit: limit || 30 }),
            contentType: 'application/json',
            dataType: 'json'
        });
    };
    API.health = function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/Health')); };
    API.initialized = true;

    function ensureStyle() {
        if (document.getElementById(styleId)) return;
        var style = document.createElement('style');
        style.id = styleId;
        style.textContent = [
            '#jf-visual-search-launcher{position:fixed;right:max(20px,env(safe-area-inset-right));bottom:max(22px,env(safe-area-inset-bottom));z-index:10000;display:inline-flex;align-items:center;gap:8px;min-height:48px;padding:0 17px;border:1px solid color-mix(in srgb,var(--theme-primary-color,#00a4dc) 70%,#fff 30%);border-radius:999px;background:linear-gradient(135deg,var(--theme-primary-color,#00a4dc),color-mix(in srgb,var(--theme-primary-color,#00a4dc) 65%,#121a24 35%));color:#fff;font-size:14px;font-weight:700;letter-spacing:.01em;box-shadow:0 8px 28px #0007;cursor:pointer;transition:transform .18s ease,box-shadow .18s ease}#jf-visual-search-launcher:hover{transform:translateY(-2px);box-shadow:0 12px 32px #0009}#jf-visual-search-launcher:focus-visible{outline:3px solid #fff8;outline-offset:3px}#jf-visual-search-launcher .jf-vs-launcher-icon{font-size:21px;line-height:1}',
            '#jf-visual-search-panel{position:fixed;inset:0;z-index:10001;display:flex;align-items:flex-start;justify-content:flex-end;padding:clamp(14px,4vh,48px) max(18px,env(safe-area-inset-right)) max(18px,env(safe-area-inset-bottom)) max(18px,env(safe-area-inset-left));box-sizing:border-box}#jf-visual-search-panel .jf-vs-backdrop{position:absolute;inset:0;background:#050a10a6;backdrop-filter:blur(4px)}#jf-visual-search-panel .jf-vs-dialog{position:relative;display:flex;flex-direction:column;width:min(650px,100%);max-height:min(860px,calc(100vh - 32px));overflow:hidden;border:1px solid #ffffff1c;border-radius:20px;background:linear-gradient(160deg,color-mix(in srgb,var(--theme-background,#202020) 94%,#fff 6%),var(--theme-background,#202020));color:var(--theme-text-color,#fff);box-shadow:0 24px 80px #000b;animation:jf-vs-in .2s ease-out}',
            '#jf-visual-search-panel .jf-vs-header{display:flex;align-items:flex-start;justify-content:space-between;gap:14px;padding:22px 22px 14px;border-bottom:1px solid #ffffff12}#jf-visual-search-panel .jf-vs-kicker{margin:0 0 5px;color:var(--theme-primary-color,#00a4dc);font-size:12px;font-weight:800;letter-spacing:.12em;text-transform:uppercase}#jf-visual-search-panel h2{margin:0;font-size:22px;line-height:1.2}#jf-visual-search-panel .jf-vs-subtitle{margin:7px 0 0;color:#ffffff99;font-size:13px;line-height:1.45}#jf-visual-search-panel .jf-vs-close{flex:0 0 auto;width:36px;height:36px;border:0;border-radius:50%;background:#ffffff12;color:inherit;font-size:23px;line-height:1;cursor:pointer}#jf-visual-search-panel .jf-vs-close:hover{background:#ffffff22}',
            '#jf-visual-search-panel .jf-vs-searchbox{padding:16px 22px 12px}#jf-visual-search-panel .jf-vs-input-wrap{display:flex;align-items:center;gap:8px;padding:5px 6px 5px 14px;border:1px solid #ffffff2b;border-radius:13px;background:#00000020;transition:border-color .18s ease,box-shadow .18s ease}#jf-visual-search-panel .jf-vs-input-wrap:focus-within{border-color:var(--theme-primary-color,#00a4dc);box-shadow:0 0 0 3px color-mix(in srgb,var(--theme-primary-color,#00a4dc) 23%,transparent)}#jf-visual-search-panel input{min-width:0;flex:1;border:0;outline:0;background:transparent;color:inherit;font:inherit;font-size:16px}#jf-visual-search-panel input::placeholder{color:#ffffff70}#jf-visual-search-panel .jf-vs-clear{display:none;width:28px;height:28px;border:0;border-radius:50%;background:#ffffff12;color:inherit;cursor:pointer}#jf-visual-search-panel .jf-vs-clear.is-visible{display:block}#jf-visual-search-panel .jf-vs-submit{display:inline-flex;align-items:center;gap:6px;min-height:38px;padding:0 14px;border:0;border-radius:9px;background:var(--theme-primary-color,#00a4dc);color:#fff;font-weight:700;cursor:pointer}#jf-visual-search-panel .jf-vs-submit:disabled{opacity:.45;cursor:not-allowed}',
            '#jf-visual-search-panel .jf-vs-hints{display:flex;flex-wrap:wrap;gap:7px;margin-top:11px}#jf-visual-search-panel .jf-vs-hint{padding:6px 10px;border:1px solid #ffffff1c;border-radius:999px;background:#ffffff08;color:#ffffffb8;font-size:12px;cursor:pointer}#jf-visual-search-panel .jf-vs-hint:hover{border-color:#ffffff45;background:#ffffff12;color:#fff}',
            '#jf-visual-search-panel .jf-vs-statusbar{display:flex;align-items:center;justify-content:space-between;gap:10px;min-height:26px;padding:0 22px 12px;color:#ffffff9c;font-size:13px}#jf-visual-search-panel .jf-vs-statusbar.is-error{color:#ff9b9b}#jf-visual-search-panel .jf-vs-spinner{display:inline-block;width:13px;height:13px;margin-right:7px;border:2px solid #ffffff3d;border-top-color:currentColor;border-radius:50%;vertical-align:-2px;animation:jf-vs-spin .8s linear infinite}',
            '#jf-visual-search-panel .jf-vs-results{flex:1;min-height:0;overflow:auto;padding:0 22px 22px;scrollbar-width:thin}#jf-visual-search-panel .jf-vs-result{display:grid;grid-template-columns:116px minmax(0,1fr);gap:14px;margin:10px 0;padding:10px;border:1px solid #ffffff12;border-radius:14px;background:#ffffff08;color:inherit;text-decoration:none;transition:transform .16s ease,border-color .16s ease,background .16s ease}#jf-visual-search-panel .jf-vs-result:hover{transform:translateY(-1px);border-color:#ffffff38;background:#ffffff10}#jf-visual-search-panel .jf-vs-thumb{position:relative;display:flex;align-items:center;justify-content:center;min-height:78px;overflow:hidden;border-radius:9px;background:linear-gradient(135deg,#16324a,#263140);color:#ffffff8a;font-size:24px}#jf-visual-search-panel .jf-vs-thumb img{width:100%;height:100%;min-height:78px;object-fit:cover}#jf-visual-search-panel .jf-vs-rank{position:absolute;top:6px;left:6px;padding:2px 6px;border-radius:999px;background:#0009;color:#fff;font-size:11px;font-weight:700}#jf-visual-search-panel .jf-vs-result-main{min-width:0}#jf-visual-search-panel .jf-vs-result-title{display:block;overflow:hidden;font-size:15px;font-weight:750;line-height:1.35;text-overflow:ellipsis;white-space:nowrap}#jf-visual-search-panel .jf-vs-result-meta{margin-top:4px;color:#ffffff80;font-size:12px}#jf-visual-search-panel .jf-vs-score{display:flex;align-items:center;gap:8px;margin-top:12px;color:#ffffffb5;font-size:12px}#jf-visual-search-panel .jf-vs-score strong{margin-left:auto;color:#fff;font-size:13px}#jf-visual-search-panel .jf-vs-bar{height:5px;margin-top:5px;overflow:hidden;border-radius:99px;background:#ffffff14}#jf-visual-search-panel .jf-vs-bar span{display:block;height:100%;border-radius:inherit;background:linear-gradient(90deg,var(--theme-primary-color,#00a4dc),#7bdcff)}#jf-visual-search-panel .jf-vs-chips{display:flex;flex-wrap:wrap;gap:5px;margin-top:9px}#jf-visual-search-panel .jf-vs-chip{padding:3px 6px;border-radius:5px;background:#ffffff10;color:#ffffff9c;font-size:11px}',
            '#jf-visual-search-panel .jf-vs-empty{padding:42px 12px;text-align:center;color:#ffffff9c}#jf-visual-search-panel .jf-vs-empty-icon{margin-bottom:10px;font-size:34px;opacity:.8}#jf-visual-search-panel .jf-vs-empty strong{display:block;margin-bottom:6px;color:#fff;font-size:15px}#jf-visual-search-panel .jf-vs-footer{padding:10px 22px 16px;border-top:1px solid #ffffff10;color:#ffffff66;font-size:11px;text-align:center}',
            '@keyframes jf-vs-in{from{opacity:0;transform:translateY(-8px) scale(.985)}to{opacity:1;transform:none}}@keyframes jf-vs-spin{to{transform:rotate(360deg)}}@media(max-width:600px){#jf-visual-search-launcher{right:max(14px,env(safe-area-inset-right));bottom:max(14px,env(safe-area-inset-bottom));width:48px;height:48px;min-height:48px;padding:0;justify-content:center;border-radius:50%}#jf-visual-search-launcher .jf-vs-launcher-label{display:none}#jf-visual-search-panel{align-items:flex-end;padding:0}#jf-visual-search-panel .jf-vs-dialog{width:100%;max-height:calc(100dvh - env(safe-area-inset-top));border-radius:20px 20px 0 0;border-bottom:0;animation:jf-vs-up .2s ease-out}#jf-visual-search-panel .jf-vs-header{padding:18px 16px 12px}#jf-visual-search-panel .jf-vs-searchbox{padding:12px 16px 8px}#jf-visual-search-panel .jf-vs-statusbar{padding:0 16px 9px}#jf-visual-search-panel .jf-vs-results{padding:0 16px 16px}#jf-visual-search-panel .jf-vs-footer{padding:9px 16px calc(12px + env(safe-area-inset-bottom))}#jf-visual-search-panel .jf-vs-result{grid-template-columns:92px minmax(0,1fr);gap:11px;padding:8px}#jf-visual-search-panel .jf-vs-result-title{font-size:14px}@keyframes jf-vs-up{from{opacity:0;transform:translateY(16px)}to{opacity:1;transform:none}}}@media(prefers-reduced-motion:reduce){#jf-visual-search-launcher,#jf-visual-search-panel .jf-vs-result{transition:none}#jf-visual-search-panel .jf-vs-dialog{animation:none}}'
        ].join('');
        document.head.appendChild(style);
    }

    function closePanel() {
        var panel = document.getElementById(panelId);
        if (panel) panel.remove();
        var launcher = document.getElementById(launcherId);
        if (launcher) launcher.setAttribute('aria-expanded', 'false');
    }

    function renderEmpty(container, title, detail) {
        container.innerHTML = '<div class="jf-vs-empty"><div class="jf-vs-empty-icon" aria-hidden="true">⌕</div><strong>' + escapeHtml(title) + '</strong><span>' + escapeHtml(detail || '') + '</span></div>';
    }

    function renderResults(container, data, query) {
        var results = prop(data, 'Results');
        results = Array.isArray(results) ? results : [];
        if (!results.length) {
            renderEmpty(container, '没有找到相近的视频', '可以换一种描述，例如人物、颜色、地点或动作。');
            return 0;
        }
        container.innerHTML = results.map(function (item, index) {
            var itemId = prop(item, 'ItemId') || '';
            var title = prop(item, 'Title') || '未命名视频';
            var score = clampScore(prop(item, 'Score'));
            var visualScore = prop(item, 'VisualScore');
            var titleScore = prop(item, 'TitleScore');
            var frame = prop(item, 'BestFrame');
            var timestamp = frame ? Number(prop(frame, 'TimestampMs') || 0) : 0;
            var detail = '#!/details?id=' + encodeURIComponent(itemId);
            var poster = itemId ? ApiClient.getUrl('Items/' + encodeURIComponent(itemId) + '/Images/Primary?fillWidth=320&quality=80') : '';
            var chips = '';
            if (visualScore != null) chips += '<span class="jf-vs-chip">画面 ' + escapeHtml(scoreText(visualScore)) + '</span>';
            if (titleScore != null) chips += '<span class="jf-vs-chip">标题 ' + escapeHtml(scoreText(titleScore)) + '</span>';
            if (frame && timestamp > 0) chips += '<span class="jf-vs-chip">命中 ' + escapeHtml(formatTimestamp(timestamp)) + '</span>';
            return '<a class="jf-vs-result" href="' + escapeHtml(detail) + '" title="打开：' + escapeHtml(title) + '">' +
                '<div class="jf-vs-thumb">' + (poster ? '<img src="' + escapeHtml(poster) + '" alt="" loading="lazy">' : '◉') + '<span class="jf-vs-rank">#' + (index + 1) + '</span></div>' +
                '<div class="jf-vs-result-main"><span class="jf-vs-result-title">' + escapeHtml(title) + '</span><div class="jf-vs-result-meta">与“' + escapeHtml(query) + '”的综合相似度</div><div class="jf-vs-score"><span>综合相关度</span><strong>' + escapeHtml(scoreText(score)) + '</strong></div><div class="jf-vs-bar"><span style="width:' + (score * 100).toFixed(1) + '%"></span></div><div class="jf-vs-chips">' + chips + '</div></div></a>';
        }).join('');
        Array.prototype.forEach.call(container.querySelectorAll('img'), function (image) {
            image.addEventListener('error', function () { image.remove(); });
        });
        return results.length;
    }

    function formatTimestamp(milliseconds) {
        var seconds = Math.max(0, Math.floor(milliseconds / 1000));
        var minutes = Math.floor(seconds / 60);
        var hours = Math.floor(minutes / 60);
        minutes %= 60;
        seconds %= 60;
        return (hours ? hours + ':' + String(minutes).padStart(2, '0') : minutes) + ':' + String(seconds).padStart(2, '0');
    }

    function openPanel() {
        ensureStyle();
        var old = document.getElementById(panelId);
        if (old) {
            var oldInput = old.querySelector('[data-jf-vs-query]');
            if (oldInput) oldInput.focus();
            return;
        }
        var panel = document.createElement('div');
        panel.id = panelId;
        panel.innerHTML = '<div class="jf-vs-backdrop" data-jf-vs-close></div><section class="jf-vs-dialog" role="dialog" aria-modal="true" aria-labelledby="jf-vs-title"><header class="jf-vs-header"><div><p class="jf-vs-kicker">Visual Search</p><h2 id="jf-vs-title">搜索视频内容</h2><p class="jf-vs-subtitle">描述画面中的人物、地点、颜色或动作，找到语义相近的视频。</p></div><button class="jf-vs-close" type="button" data-jf-vs-close aria-label="关闭">×</button></header><div class="jf-vs-searchbox"><div class="jf-vs-input-wrap"><input type="search" data-jf-vs-query autocomplete="off" enterkeyhint="search" placeholder="例如：海边的金发女性、红色衣服"><button class="jf-vs-clear" type="button" data-jf-vs-clear aria-label="清空">×</button><button class="jf-vs-submit" type="button" data-jf-vs-search><span aria-hidden="true">⌕</span><span>搜索</span></button></div><div class="jf-vs-hints"><button class="jf-vs-hint" type="button" data-jf-vs-hint="海边的户外场景">海边户外</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="红色衣服">红色衣服</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="家庭聚会">家庭聚会</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="两个人在说话">两个人说话</button></div></div><div class="jf-vs-statusbar"><span data-jf-vs-status>输入描述开始搜索</span><span data-jf-vs-scope></span></div><div class="jf-vs-results" data-jf-vs-results><div class="jf-vs-empty"><div class="jf-vs-empty-icon" aria-hidden="true">✦</div><strong>从一个画面描述开始</strong><span>搜索结果会按照综合相关度展示。</span></div></div><footer class="jf-vs-footer">Enter 搜索 · Ctrl/⌘ + Shift + K 打开 · Esc 关闭</footer></section>';
        document.body.appendChild(panel);
        var input = panel.querySelector('[data-jf-vs-query]');
        var submit = panel.querySelector('[data-jf-vs-search]');
        var clear = panel.querySelector('[data-jf-vs-clear]');
        var status = panel.querySelector('[data-jf-vs-status]');
        var scope = panel.querySelector('[data-jf-vs-scope]');
        var resultsContainer = panel.querySelector('[data-jf-vs-results]');
        var libraryIds = currentLibraryIds();
        if (libraryIds.length) scope.textContent = '当前媒体库';
        var updateInput = function () {
            var hasValue = String(input.value || '').trim().length > 0;
            submit.disabled = !hasValue;
            clear.classList.toggle('is-visible', hasValue);
        };
        var run = async function () {
            var value = String(input.value || '').trim();
            if (!value) { input.focus(); return; }
            submit.disabled = true;
            status.classList.remove('is-error');
            status.innerHTML = '<span class="jf-vs-spinner" aria-hidden="true"></span>正在理解并搜索…';
            renderEmpty(resultsContainer, '正在搜索', '正在比较标题和 Trickplay 画面向量。');
            try {
                var data = await API.search(value, libraryIds, 30);
                var count = renderResults(resultsContainer, data, value);
                status.textContent = count ? '找到 ' + count + ' 个相近视频' : '没有匹配结果';
            } catch (error) {
                status.classList.add('is-error');
                status.textContent = '搜索失败：' + await errorMessage(error);
                renderEmpty(resultsContainer, '暂时无法完成搜索', '请检查 Embedding 服务和 Qdrant 连接后重试。');
            } finally {
                updateInput();
            }
        };
        input.addEventListener('input', updateInput);
        input.addEventListener('keydown', function (event) { if (event.key === 'Enter') { event.preventDefault(); run(); } });
        submit.addEventListener('click', run);
        clear.addEventListener('click', function () { input.value = ''; updateInput(); input.focus(); });
        Array.prototype.forEach.call(panel.querySelectorAll('[data-jf-vs-hint]'), function (hint) {
            hint.addEventListener('click', function () { input.value = hint.getAttribute('data-jf-vs-hint') || ''; updateInput(); input.focus(); });
        });
        Array.prototype.forEach.call(panel.querySelectorAll('[data-jf-vs-close]'), function (element) {
            element.addEventListener('click', function (event) { if (event.target === element || element.classList.contains('jf-vs-close')) closePanel(); });
        });
        updateInput();
        window.requestAnimationFrame(function () { input.focus(); });
    }

    function ensureLauncher() {
        if (!document.body || document.getElementById(launcherId)) return;
        ensureStyle();
        var button = document.createElement('button');
        button.id = launcherId;
        button.type = 'button';
        button.title = '语义搜索（Ctrl/⌘ + Shift + K）';
        button.setAttribute('aria-label', '打开视频语义搜索');
        button.setAttribute('aria-expanded', 'false');
        button.innerHTML = '<span class="jf-vs-launcher-icon" aria-hidden="true">⌕</span><span class="jf-vs-launcher-label">语义搜索</span>';
        button.addEventListener('click', function () { button.setAttribute('aria-expanded', 'true'); openPanel(); });
        document.body.appendChild(button);
    }

    document.addEventListener('keydown', function (event) {
        if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'k') {
            event.preventDefault();
            ensureLauncher();
            var launcher = document.getElementById(launcherId);
            if (launcher) launcher.setAttribute('aria-expanded', 'true');
            openPanel();
        } else if (event.key === 'Escape' && document.getElementById(panelId)) {
            closePanel();
        }
    });

    function scheduleEnsure() {
        if (observerTimer) return;
        observerTimer = window.setTimeout(function () { observerTimer = 0; ensureLauncher(); }, 180);
    }

    ensureLauncher();
    if (document.body && window.MutationObserver) new MutationObserver(scheduleEnsure).observe(document.body, { childList: true, subtree: true });
})();
