(function () {
    'use strict';
    if (window.__jellyfinVisualSearchLoaded) return;
    window.__jellyfinVisualSearchLoaded = true;

    var API = window.JellyfinVisualSearch = window.JellyfinVisualSearch || {};
    var launcherId = 'jf-visual-search-launcher';
    var panelId = 'jf-visual-search-panel';
    var styleId = 'jf-visual-search-style';
    var observerTimer = 0;
    var observerAttached = false;
    var settings = { resultLimit: 30, defaultPresetId: 'balanced', presets: [], customCode: '', display: {} };

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

    function scoreText(value) { return (clampScore(value) * 100).toFixed(1) + '%'; }

    function currentLibraryIds() {
        var hash = String(window.location.hash || '');
        var queryIndex = hash.indexOf('?');
        if (queryIndex < 0) return [];
        var id = '';
        hash.slice(queryIndex + 1).split('&').some(function (part) {
            var pair = part.split('=');
            if (pair[0] !== 'topParentId') return false;
            try { id = decodeURIComponent(pair.slice(1).join('=')); } catch (_) { id = ''; }
            return true;
        });
        return id && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(id) ? [id] : [];
    }

    async function errorMessage(error) {
        if (error && typeof error.text === 'function' && typeof error.status === 'number') {
            var raw = '';
            try { raw = await error.text(); } catch (_) { }
            var body = null;
            try { body = raw ? JSON.parse(raw) : null; } catch (_) { }
            var detail = prop(body, 'Message') || prop(body, 'Error') || prop(body, 'message') || raw || error.statusText || '请求失败';
            return 'HTTP ' + error.status + '：' + detail;
        }
        var legacyBody = error && (error.responseJSON || (error.response && error.response.data) || error.data);
        return prop(legacyBody, 'Message') || prop(legacyBody, 'Error') || prop(legacyBody, 'message') || (error && error.message) || String(error);
    }

    API.search = function (query, libraryIds, limit, presetId) {
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('VisualSearch/Search'), data: JSON.stringify({ query: query, libraryIds: libraryIds || [], limit: limit || settings.resultLimit || 30, presetId: presetId || settings.defaultPresetId }), contentType: 'application/json', dataType: 'json' });
    };
    API.presets = function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/SearchPresets')); };
    API.health = function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/Health')); };
    API.initialized = true;

    function ensureStyle() {
        if (document.getElementById(styleId)) return;
        var style = document.createElement('style');
        style.id = styleId;
        style.textContent = [
            '#jf-visual-search-launcher{position:fixed;right:20px;bottom:22px;right:max(20px,env(safe-area-inset-right));bottom:max(22px,env(safe-area-inset-bottom));z-index:10000;display:inline-flex;align-items:center;gap:8px;min-height:48px;padding:0 17px;border:1px solid #ffffff45;border-radius:999px;background:linear-gradient(135deg,var(--theme-primary-color,#00a4dc),#12607e);color:#fff;font-size:14px;font-weight:750;letter-spacing:.01em;box-shadow:0 8px 28px #0007;cursor:pointer;transition:transform .18s ease,box-shadow .18s ease}#jf-visual-search-launcher:hover{transform:translateY(-2px);box-shadow:0 12px 32px #0009}#jf-visual-search-launcher:focus-visible{outline:3px solid #fff8;outline-offset:3px}#jf-visual-search-launcher .jf-vs-launcher-icon{font-size:21px;line-height:1}',
            '#jf-visual-search-panel{position:fixed;inset:0;z-index:10001;overflow:auto;background:radial-gradient(ellipse at 10% -10%,#0a6c9140,transparent 42%),linear-gradient(145deg,#101820f2,var(--theme-background,#202020) 45%,#0d1218f7);color:var(--theme-text-color,#fff);animation:jf-vs-in .18s ease-out}#jf-visual-search-panel .jf-vs-shell{width:min(1240px,100%);min-height:100%;box-sizing:border-box;margin:0 auto;padding:clamp(18px,3vw,40px) clamp(14px,3vw,38px) 50px}#jf-visual-search-panel .jf-vs-header{display:flex;align-items:flex-start;justify-content:space-between;gap:18px;margin-bottom:18px}#jf-visual-search-panel .jf-vs-kicker{margin:0 0 5px;color:var(--theme-primary-color,#00a4dc);font-size:11px;font-weight:850;letter-spacing:.16em;text-transform:uppercase}#jf-visual-search-panel h1{margin:0;font-size:clamp(25px,4vw,38px);line-height:1.12}#jf-visual-search-panel .jf-vs-subtitle{max-width:720px;margin:9px 0 0;color:#ffffff99;font-size:13px;line-height:1.5}#jf-visual-search-panel .jf-vs-close{width:40px;height:40px;flex:0 0 auto;border:1px solid #ffffff25;border-radius:50%;background:#ffffff0d;color:inherit;font-size:24px;line-height:1;cursor:pointer}#jf-visual-search-panel .jf-vs-close:hover{background:#ffffff1e}',
            '#jf-visual-search-panel .jf-vs-searchbar{display:flex;align-items:center;gap:9px;padding:7px 8px 7px 16px;border:1px solid #ffffff2b;border-radius:14px;background:#00000030;box-shadow:0 12px 38px #0003;transition:border-color .18s ease,box-shadow .18s ease}#jf-visual-search-panel .jf-vs-searchbar:focus-within{border-color:var(--theme-primary-color,#00a4dc);box-shadow:0 0 0 3px #00a4dc2b,0 12px 38px #0003}#jf-visual-search-panel input{min-width:0;flex:1;border:0;outline:0;background:transparent;color:inherit;font:inherit;font-size:17px}#jf-visual-search-panel input::placeholder{color:#ffffff66}#jf-visual-search-panel .jf-vs-clear{display:none;width:30px;height:30px;border:0;border-radius:50%;background:#ffffff12;color:inherit;cursor:pointer}#jf-visual-search-panel .jf-vs-clear.is-visible{display:block}#jf-visual-search-panel .jf-vs-submit,#jf-visual-search-panel .jf-vs-toolbar button.jf-vs-primary{display:inline-flex;align-items:center;justify-content:center;gap:6px;min-height:40px;padding:0 16px;border:0;border-radius:10px;background:var(--theme-primary-color,#00a4dc);color:#fff;font:inherit;font-weight:750;cursor:pointer}#jf-visual-search-panel button:disabled{opacity:.45;cursor:not-allowed}',
            '#jf-visual-search-panel .jf-vs-options{display:flex;align-items:center;gap:9px;flex-wrap:wrap;margin:12px 0 18px;color:#ffffff9c;font-size:12px}#jf-visual-search-panel .jf-vs-options label{display:flex;align-items:center;gap:7px}#jf-visual-search-panel select{min-height:34px;max-width:240px;padding:6px 10px;border:1px solid #ffffff26;border-radius:9px;background:#18242d;color:inherit;font:inherit}#jf-visual-search-panel .jf-vs-hints{display:flex;flex-wrap:wrap;gap:7px;margin-left:auto}#jf-visual-search-panel .jf-vs-hint{padding:6px 10px;border:1px solid #ffffff1e;border-radius:999px;background:#ffffff08;color:#ffffffbd;font-size:12px;cursor:pointer}#jf-visual-search-panel .jf-vs-hint:hover{background:#ffffff15;color:#fff}',
            '#jf-visual-search-panel .jf-vs-toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin:4px 0 14px;padding:12px 14px;border:1px solid #ffffff16;border-radius:13px;background:#ffffff08}#jf-visual-search-panel .jf-vs-toolbar-left,#jf-visual-search-panel .jf-vs-toolbar-right{display:flex;align-items:center;gap:8px;flex-wrap:wrap}#jf-visual-search-panel .jf-vs-toolbar button{min-height:35px;padding:6px 11px;border:1px solid #ffffff22;border-radius:8px;background:#ffffff0d;color:inherit;font:inherit;font-size:12px;font-weight:700;cursor:pointer}#jf-visual-search-panel .jf-vs-toolbar button:hover{background:#ffffff19}#jf-visual-search-panel .jf-vs-count{color:#ffffff9c;font-size:12px}',
            '#jf-visual-search-panel .jf-vs-results{display:grid;grid-template-columns:repeat(auto-fill,minmax(174px,1fr));gap:16px}#jf-visual-search-panel .jf-vs-card{position:relative;display:flex;min-width:0;flex-direction:column;overflow:hidden;border:1px solid #ffffff14;border-radius:14px;background:linear-gradient(180deg,#ffffff0b,#ffffff05);box-shadow:0 8px 22px #0002;transition:transform .16s ease,border-color .16s ease,box-shadow .16s ease}#jf-visual-search-panel .jf-vs-card:hover{transform:translateY(-3px);border-color:#ffffff3b;box-shadow:0 14px 30px #0005}#jf-visual-search-panel .jf-vs-poster{position:relative;aspect-ratio:16/10;overflow:hidden;background:linear-gradient(135deg,#17354a,#263140);cursor:pointer}#jf-visual-search-panel .jf-vs-poster img{width:100%;height:100%;display:block;object-fit:cover}#jf-visual-search-panel .jf-vs-poster-fallback{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:#ffffff6b;font-size:34px}#jf-visual-search-panel .jf-vs-rank{position:absolute;top:8px;left:8px;padding:3px 7px;border-radius:999px;background:#000b;color:#fff;font-size:11px;font-weight:800}#jf-visual-search-panel .jf-vs-play{position:absolute;right:8px;bottom:8px;width:35px;height:35px;border:1px solid #ffffff55;border-radius:50%;background:#0009;color:#fff;font-size:16px;cursor:pointer}#jf-visual-search-panel .jf-vs-play:hover{background:var(--theme-primary-color,#00a4dc)}#jf-visual-search-panel .jf-vs-card-body{display:flex;min-width:0;flex:1;flex-direction:column;padding:11px 11px 10px}#jf-visual-search-panel .jf-vs-card-title{display:block;overflow:hidden;color:inherit;text-decoration:none;font-size:14px;font-weight:750;line-height:1.35;text-overflow:ellipsis;white-space:nowrap}#jf-visual-search-panel .jf-vs-card-title:hover{color:var(--theme-primary-color,#00a4dc)}#jf-visual-search-panel .jf-vs-meta{margin-top:4px;color:#ffffff73;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}#jf-visual-search-panel .jf-vs-score{display:flex;align-items:center;gap:7px;margin-top:auto;padding-top:10px;color:#ffffffa8;font-size:11px}#jf-visual-search-panel .jf-vs-score strong{margin-left:auto;color:#fff;font-size:12px}#jf-visual-search-panel .jf-vs-bar{height:4px;margin-top:5px;overflow:hidden;border-radius:99px;background:#ffffff14}#jf-visual-search-panel .jf-vs-bar span{display:block;height:100%;border-radius:inherit;background:linear-gradient(90deg,var(--theme-primary-color,#00a4dc),#7bdcff)}#jf-visual-search-panel .jf-vs-chips{display:flex;flex-wrap:wrap;gap:5px;margin-top:8px}#jf-visual-search-panel .jf-vs-chip{max-width:100%;padding:3px 6px;overflow:hidden;border-radius:5px;background:#ffffff10;color:#ffffff9e;font-size:10px;text-overflow:ellipsis;white-space:nowrap}#jf-visual-search-panel .jf-vs-card-actions{display:flex;gap:6px;margin-top:9px}#jf-visual-search-panel .jf-vs-card-actions button{min-width:0;flex:1;padding:6px 5px;border:1px solid #ffffff1a;border-radius:7px;background:#ffffff08;color:#ffffffbe;font-size:11px;cursor:pointer}#jf-visual-search-panel .jf-vs-card-actions button:hover{background:#ffffff18;color:#fff}',
            '#jf-visual-search-panel .jf-vs-status{min-height:22px;color:#ffffff9e;font-size:13px}#jf-visual-search-panel .jf-vs-status.is-error{color:#ffaaaa}#jf-visual-search-panel .jf-vs-spinner{display:inline-block;width:13px;height:13px;margin-right:7px;border:2px solid #ffffff3d;border-top-color:currentColor;border-radius:50%;vertical-align:-2px;animation:jf-vs-spin .8s linear infinite}#jf-visual-search-panel .jf-vs-empty{grid-column:1/-1;padding:72px 12px;text-align:center;color:#ffffff9c}#jf-visual-search-panel .jf-vs-empty-icon{margin-bottom:12px;color:var(--theme-primary-color,#00a4dc);font-size:40px}#jf-visual-search-panel .jf-vs-empty strong{display:block;margin-bottom:7px;color:#fff;font-size:16px}#jf-visual-search-panel .jf-vs-empty span{font-size:13px}#jf-visual-search-panel .jf-vs-footer{margin-top:26px;color:#ffffff54;font-size:11px;text-align:center}',
            '@keyframes jf-vs-in{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}@keyframes jf-vs-spin{to{transform:rotate(360deg)}}@media(max-width:600px){#jf-visual-search-launcher{right:14px;bottom:14px;right:max(14px,env(safe-area-inset-right));bottom:max(14px,env(safe-area-inset-bottom));width:48px;height:48px;min-height:48px;padding:0;justify-content:center;border-radius:50%}#jf-visual-search-launcher .jf-vs-launcher-label{display:none}#jf-visual-search-panel .jf-vs-shell{padding:16px 12px calc(32px + env(safe-area-inset-bottom))}#jf-visual-search-panel .jf-vs-header{margin-bottom:14px}#jf-visual-search-panel .jf-vs-subtitle{font-size:12px}#jf-visual-search-panel .jf-vs-searchbar{padding-left:12px}#jf-visual-search-panel .jf-vs-submit{width:42px;padding:0;font-size:0}#jf-visual-search-panel .jf-vs-submit:first-letter{font-size:17px}#jf-visual-search-panel .jf-vs-hints{width:100%;margin-left:0}#jf-visual-search-panel .jf-vs-options{align-items:flex-start}#jf-visual-search-panel .jf-vs-options label{width:100%}#jf-visual-search-panel select{max-width:none;width:100%}#jf-visual-search-panel .jf-vs-results{grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}#jf-visual-search-panel .jf-vs-card{border-radius:11px}#jf-visual-search-panel .jf-vs-card-body{padding:8px}#jf-visual-search-panel .jf-vs-card-title{font-size:12px}#jf-visual-search-panel .jf-vs-card-actions button{font-size:10px;padding:6px 3px}#jf-visual-search-panel .jf-vs-toolbar-left,#jf-visual-search-panel .jf-vs-toolbar-right{width:100%}#jf-visual-search-panel .jf-vs-toolbar-right button{flex:1}}@media(prefers-reduced-motion:reduce){#jf-visual-search-panel,#jf-visual-search-panel .jf-vs-card,#jf-visual-search-launcher{animation:none;transition:none}}'
        ].join('');
        (document.head || document.documentElement).appendChild(style);
    }

    function closePanel() {
        var panel = document.getElementById(panelId);
        if (panel) panel.remove();
        var launcher = document.getElementById(launcherId);
        if (launcher) launcher.setAttribute('aria-expanded', 'false');
    }

    function formatTimestamp(milliseconds) {
        var seconds = Math.max(0, Math.floor(Number(milliseconds || 0) / 1000));
        var minutes = Math.floor(seconds / 60);
        var hours = Math.floor(minutes / 60);
        minutes %= 60;
        seconds %= 60;
        var pad2 = function (value) { return value < 10 ? '0' + value : String(value); };
        return (hours ? hours + ':' + pad2(minutes) : minutes) + ':' + pad2(seconds);
    }

    function runtimeText(ticks) { return ticks ? formatTimestamp(Number(ticks) / 10000) : ''; }

    function imageUrl(item) {
        var itemId = prop(item, 'ItemId') || '';
        var frame = prop(item, 'BestFrame');
        var frameIndex = frame ? Number(prop(frame, 'FrameIndex')) : -1;
        var showFrame = settings.display && prop(settings.display, 'BestFramePoster') !== false;
        if (showFrame && itemId && frameIndex >= 0) return ApiClient.getUrl('Videos/' + encodeURIComponent(itemId) + '/Trickplay/320/' + frameIndex + '.jpg');
        return itemId ? ApiClient.getUrl('Items/' + encodeURIComponent(itemId) + '/Images/Primary?fillWidth=420&quality=82') : '';
    }

    function getPlaybackManager() {
        if (window.playbackManager) return window.playbackManager;
        try { if (typeof playbackManager !== 'undefined') return playbackManager; } catch (_) { }
        return null;
    }

    function getPlaybackServerId() {
        try {
            return ApiClient && typeof ApiClient.serverId === 'function' ? ApiClient.serverId() : '';
        } catch (_) {
            return '';
        }
    }

    function getPlaybackIds(results) {
        var seen = Object.create(null);
        return (results || []).map(function (item) { return String(prop(item, 'ItemId') || ''); }).filter(function (id) {
            if (!id || seen[id]) return false;
            seen[id] = true;
            return true;
        });
    }

    async function playResults(results, startItem) {
        var manager = getPlaybackManager();
        var ids = getPlaybackIds(results);
        var serverId = getPlaybackServerId();
        if (!manager || !ids.length || !serverId) { if (startItem) window.location.hash = '#!/details?id=' + encodeURIComponent(prop(startItem, 'ItemId')); return; }
        // Let Jellyfin's PlaybackManager fetch the complete DTO in one batch and
        // construct the native playback queue. Fetching every item separately
        // here can issue dozens of /Items/{id} requests and leave Play All stuck.
        var options = { ids: ids, serverId: serverId, autoplay: true };
        if (startItem && prop(startItem, 'BestFrame')) options.startPositionTicks = Math.max(0, Number(prop(prop(startItem, 'BestFrame'), 'TimestampMs') || 0) * 10000);
        try { await manager.play(options); } catch (error) { console.error('[Visual Search] playback failed', error); if (startItem) window.location.hash = '#!/details?id=' + encodeURIComponent(prop(startItem, 'ItemId')); }
    }

    async function queueResults(results) {
        var manager = getPlaybackManager();
        if (!manager) return;
        var ids = getPlaybackIds(results);
        var serverId = getPlaybackServerId();
        if (!ids.length || !serverId) return;
        // Use the same batch path for queueing; PlaybackManager handles the
        // current player and native queue semantics for us.
        try { await manager.queue({ ids: ids, serverId: serverId }); } catch (error) { console.error('[Visual Search] queue failed', error); }
    }

    function applyCustomCode(results, query, preset) {
        if (!preset || String(prop(preset, 'Mode') || prop(preset, 'mode') || '').toLowerCase() !== 'custom') return results;
        var code = String(settings.customCode || '').trim();
        if (!code) return results;
        try {
            var helper = { clamp: clampScore, scoreText: scoreText, timestamp: formatTimestamp, isCover: function (item) { return String(prop(item, 'VisualSource') || prop(item, 'visualSource') || '').toLowerCase() === 'primary_cover'; } };
            var fn = new Function('items', 'query', 'preset', 'helpers', code);
            var output = fn(results.slice(), query, preset, helper);
            if (!Array.isArray(output)) throw new Error('自定义代码必须返回数组 items');
            return output.filter(function (item) { return item && prop(item, 'ItemId'); });
        } catch (error) { throw new Error('自定义排序代码执行失败：' + error.message); }
    }

    function sortResults(results, mode) {
        var copy = results.slice();
        if (mode === 'visual') copy.sort(function (a, b) { return Number(prop(b, 'VisualScore') || -1) - Number(prop(a, 'VisualScore') || -1); });
        else if (mode === 'title') copy.sort(function (a, b) { return Number(prop(b, 'TitleScore') || -1) - Number(prop(a, 'TitleScore') || -1); });
        else if (mode === 'time') copy.sort(function (a, b) { return Number(prop(prop(a, 'BestFrame'), 'TimestampMs') || 0) - Number(prop(prop(b, 'BestFrame'), 'TimestampMs') || 0); });
        return copy;
    }

    function renderEmpty(container, title, detail) {
        container.innerHTML = '<div class="jf-vs-empty"><div class="jf-vs-empty-icon" aria-hidden="true">⌕</div><strong>' + escapeHtml(title) + '</strong><span>' + escapeHtml(detail || '') + '</span></div>';
    }

    function renderResults(container, data, query, sortMode) {
        var results = prop(data, 'Results');
        results = Array.isArray(results) ? results.slice() : [];
        var presetId = String(prop(data, 'PresetId') || '').toLowerCase();
        var preset = (settings.presets || []).filter(function (item) { return String(prop(item, 'Id') || '').toLowerCase() === presetId; })[0];
        var isCustom = preset && String(prop(preset, 'Mode') || prop(preset, 'mode') || '').toLowerCase() === 'custom';
        // Custom code receives the complete server-side candidate pool first;
        // only its final output is limited to the number shown in the panel.
        results = isCustom ? applyCustomCode(results, query, preset) : sortResults(results, sortMode || 'score');
        results = results.slice(0, Math.max(1, Math.min(100, Number(prop(data, 'ResultLimit') || settings.resultLimit || 30))));
        container.__jfResults = results;
        if (!results.length) { renderEmpty(container, '没有找到相近的视频', '可以换一种描述，例如人物、颜色、地点或动作。'); return 0; }
        var display = settings.display || {};
        var showRank = prop(display, 'Rank') !== false;
        var showScore = prop(display, 'ScoreBreakdown') !== false;
        var showTime = prop(display, 'BestFrameTimestamp') !== false;
        container.innerHTML = results.map(function (item, index) {
            var itemId = prop(item, 'ItemId') || '';
            var title = prop(item, 'Title') || '未命名视频';
            var score = clampScore(prop(item, 'Score'));
            var visualScore = prop(item, 'VisualScore');
            var titleScore = prop(item, 'TitleScore');
            var frame = prop(item, 'BestFrame');
            var timestamp = frame ? Number(prop(frame, 'TimestampMs') || 0) : 0;
            var detail = '#!/details?id=' + encodeURIComponent(itemId);
            var poster = imageUrl(item);
            var chips = '';
            if (showScore && visualScore != null) chips += '<span class="jf-vs-chip">画面 ' + escapeHtml(scoreText(visualScore)) + '</span>';
            if (showScore && titleScore != null) chips += '<span class="jf-vs-chip">标题 ' + escapeHtml(scoreText(titleScore)) + '</span>';
            if (showTime && frame && timestamp > 0) chips += '<span class="jf-vs-chip">命中 ' + escapeHtml(formatTimestamp(timestamp)) + '</span>';
            var source = String(prop(item, 'VisualSource') || '').toLowerCase();
            if (source === 'primary_cover') chips += '<span class="jf-vs-chip">封面命中</span>';
            else if (source === 'trickplay') chips += '<span class="jf-vs-chip">Trickplay 命中</span>';
            var meta = runtimeText(prop(item, 'RunTimeTicks')) || (prop(item, 'MatchKind') === 'visual' ? '画面命中' : prop(item, 'MatchKind') === 'title' ? '标题命中' : '标题 + 画面');
            return '<article class="jf-vs-card" data-jf-vs-item="' + escapeHtml(itemId) + '"><div class="jf-vs-poster" data-jf-vs-play-item="' + escapeHtml(itemId) + '">' + (poster ? '<img src="' + escapeHtml(poster) + '" alt="" loading="lazy" data-jf-vs-image="1">' : '<span class="jf-vs-poster-fallback">◉</span>') + (showRank ? '<span class="jf-vs-rank">#' + (index + 1) + '</span>' : '') + '<button class="jf-vs-play" type="button" data-jf-vs-play-item="' + escapeHtml(itemId) + '" aria-label="播放">▶</button></div><div class="jf-vs-card-body"><a class="jf-vs-card-title" href="' + escapeHtml(detail) + '" title="' + escapeHtml(title) + '">' + escapeHtml(title) + '</a><div class="jf-vs-meta">' + escapeHtml(meta) + '</div><div class="jf-vs-score"><span>相关度</span><strong>' + escapeHtml(scoreText(score)) + '</strong></div><div class="jf-vs-bar"><span style="width:' + (score * 100).toFixed(1) + '%"></span></div><div class="jf-vs-chips">' + chips + '</div><div class="jf-vs-card-actions">' + (showTime && frame && timestamp > 0 ? '<button type="button" data-jf-vs-play-item="' + escapeHtml(itemId) + '">从 ' + escapeHtml(formatTimestamp(timestamp)) + ' 播放</button>' : '<button type="button" data-jf-vs-play-item="' + escapeHtml(itemId) + '">播放</button>') + (prop(display, 'QueueAction') !== false ? '<button type="button" data-jf-vs-queue-item="' + escapeHtml(itemId) + '">加入队列</button>' : '') + '</div></div></article>';
        }).join('');
        Array.prototype.forEach.call(container.querySelectorAll('img[data-jf-vs-image]'), function (image, index) { image.addEventListener('error', function () { var item = results[index] || {}; var id = prop(item, 'ItemId'); var primary = id ? ApiClient.getUrl('Items/' + encodeURIComponent(id) + '/Images/Primary?fillWidth=420&quality=82') : ''; if (primary && image.src !== primary) image.src = primary; else image.style.display = 'none'; }); });
        return results.length;
    }

    async function loadSettings() {
        try {
            var data = await API.presets();
            settings.resultLimit = Math.max(1, Math.min(100, Number(prop(data, 'ResultLimit') || 30)));
            settings.defaultPresetId = prop(data, 'DefaultPresetId') || 'balanced';
            settings.presets = prop(data, 'Presets') || [];
            settings.customCode = prop(data, 'CustomCode') || '';
            settings.display = prop(data, 'Display') || {};
        } catch (_) { }
        return settings;
    }

    function openPanel() {
        ensureStyle();
        var old = document.getElementById(panelId);
        if (old) { var oldInput = old.querySelector('[data-jf-vs-query]'); if (oldInput) oldInput.focus(); return; }
        var panel = document.createElement('div'); panel.id = panelId;
        panel.innerHTML = '<div class="jf-vs-shell"><header class="jf-vs-header"><div><p class="jf-vs-kicker">Visual Search</p><h1>视频语义搜索</h1><p class="jf-vs-subtitle">按画面和标题寻找视频。结果按文件夹式网格展示，支持播放全部、加入播放队列和从命中时间点开始播放。</p></div><button class="jf-vs-close" type="button" data-jf-vs-close aria-label="关闭">×</button></header><div class="jf-vs-searchbar"><input type="search" data-jf-vs-query autocomplete="off" enterkeyhint="search" placeholder="描述画面中的人物、地点、颜色或动作"><button class="jf-vs-clear" type="button" data-jf-vs-clear aria-label="清空">×</button><button class="jf-vs-submit" type="button" data-jf-vs-search>⌕<span>搜索</span></button></div><div class="jf-vs-options"><label>搜索配置<select data-jf-vs-preset></select></label><label>结果排序<select data-jf-vs-sort><option value="score">综合相关度</option><option value="visual">画面分</option><option value="title">标题分</option><option value="time">命中时间</option></select></label><div class="jf-vs-hints"><button class="jf-vs-hint" type="button" data-jf-vs-hint="海边的户外场景">海边户外</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="红色衣服">红色衣服</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="家庭聚会">家庭聚会</button><button class="jf-vs-hint" type="button" data-jf-vs-hint="两个人在说话">两个人说话</button></div></div><div class="jf-vs-toolbar"><div class="jf-vs-toolbar-left"><span class="jf-vs-count" data-jf-vs-status>输入描述开始搜索</span><span class="jf-vs-count" data-jf-vs-scope></span></div><div class="jf-vs-toolbar-right"><button type="button" class="jf-vs-primary" data-jf-vs-play-all style="display:none">▶ 播放全部</button><button type="button" data-jf-vs-queue-all style="display:none">＋ 加入队列</button></div></div><main class="jf-vs-results" data-jf-vs-results><div class="jf-vs-empty"><div class="jf-vs-empty-icon" aria-hidden="true">✦</div><strong>从一个画面描述开始</strong><span>搜索结果会按照当前配置展示。</span></div></main><footer class="jf-vs-footer">Enter 搜索 · Ctrl/⌘ + Shift + K 打开 · Esc 关闭</footer></div>';
        document.body.appendChild(panel);
        var input = panel.querySelector('[data-jf-vs-query]'), submit = panel.querySelector('[data-jf-vs-search]'), clear = panel.querySelector('[data-jf-vs-clear]'), presetSelect = panel.querySelector('[data-jf-vs-preset]'), sortSelect = panel.querySelector('[data-jf-vs-sort]'), status = panel.querySelector('[data-jf-vs-status]'), scope = panel.querySelector('[data-jf-vs-scope]'), resultsContainer = panel.querySelector('[data-jf-vs-results]'), playAllButton = panel.querySelector('[data-jf-vs-play-all]'), queueAllButton = panel.querySelector('[data-jf-vs-queue-all]');
        var libraryIds = currentLibraryIds(); if (libraryIds.length) scope.textContent = '当前媒体库';
        (settings.presets || []).forEach(function (preset) { var option = document.createElement('option'); option.value = prop(preset, 'Id') || ''; option.textContent = prop(preset, 'Name') || option.value; option.title = prop(preset, 'Description') || ''; presetSelect.appendChild(option); }); presetSelect.value = settings.defaultPresetId;
        var updateInput = function () { var hasValue = String(input.value || '').trim().length > 0; submit.disabled = !hasValue; clear.classList.toggle('is-visible', hasValue); };
        var updateActions = function () { var hasResults = resultsContainer.__jfResults && resultsContainer.__jfResults.length; playAllButton.style.display = hasResults && prop(settings.display, 'PlayAll') !== false ? '' : 'none'; queueAllButton.style.display = hasResults && prop(settings.display, 'QueueAction') !== false ? '' : 'none'; };
        var rerender = function () { if (resultsContainer.__jfData) renderResults(resultsContainer, resultsContainer.__jfData, resultsContainer.__jfQuery, sortSelect.value); updateActions(); };
        var run = async function () { var value = String(input.value || '').trim(); if (!value) { input.focus(); return; } submit.disabled = true; status.classList.remove('is-error'); status.innerHTML = '<span class="jf-vs-spinner" aria-hidden="true"></span>正在理解并搜索…'; renderEmpty(resultsContainer, '正在搜索', '正在比较标题、Trickplay 画面和 Primary 封面。'); try { var data = await API.search(value, libraryIds, settings.resultLimit, presetSelect.value); resultsContainer.__jfData = data; resultsContainer.__jfQuery = value; var count = renderResults(resultsContainer, data, value, sortSelect.value); updateActions(); status.textContent = count ? '找到 ' + count + ' 个相近视频 · ' + (prop(data, 'PresetName') || '') : '没有匹配结果'; } catch (error) { status.classList.add('is-error'); status.textContent = '搜索失败：' + await errorMessage(error); renderEmpty(resultsContainer, '暂时无法完成搜索', '请检查 Embedding 服务和 Qdrant 连接后重试。'); updateActions(); } finally { updateInput(); } };
        input.addEventListener('input', updateInput); input.addEventListener('keydown', function (event) { if (event.key === 'Enter') { event.preventDefault(); run(); } }); submit.addEventListener('click', run); clear.addEventListener('click', function () { input.value = ''; updateInput(); input.focus(); }); presetSelect.addEventListener('change', function () { if (String(input.value || '').trim()) run(); }); sortSelect.addEventListener('change', rerender);
        panel.querySelectorAll('[data-jf-vs-hint]').forEach(function (hint) { hint.addEventListener('click', function () { input.value = hint.getAttribute('data-jf-vs-hint') || ''; updateInput(); input.focus(); run(); }); }); panel.querySelectorAll('[data-jf-vs-close]').forEach(function (element) { element.addEventListener('click', closePanel); });
        resultsContainer.addEventListener('click', function (event) { var play = event.target.closest ? event.target.closest('[data-jf-vs-play-item]') : null; var queue = event.target.closest ? event.target.closest('[data-jf-vs-queue-item]') : null; if (play) { event.preventDefault(); event.stopPropagation(); var item = (resultsContainer.__jfResults || []).filter(function (x) { return String(prop(x, 'ItemId')) === String(play.getAttribute('data-jf-vs-play-item')); })[0]; if (item) playResults([item], item); } if (queue) { event.preventDefault(); event.stopPropagation(); var queued = (resultsContainer.__jfResults || []).filter(function (x) { return String(prop(x, 'ItemId')) === String(queue.getAttribute('data-jf-vs-queue-item')); }); if (queued.length) queueResults(queued); } }); playAllButton.addEventListener('click', function () { playResults(resultsContainer.__jfResults || []); }); queueAllButton.addEventListener('click', function () { queueResults(resultsContainer.__jfResults || []); });
        updateInput(); if (window.requestAnimationFrame) window.requestAnimationFrame(function () { input.focus(); }); else window.setTimeout(function () { input.focus(); }, 0); loadSettings().then(function () { if (!presetSelect.options.length) (settings.presets || []).forEach(function (preset) { var option = document.createElement('option'); option.value = prop(preset, 'Id') || ''; option.textContent = prop(preset, 'Name') || option.value; presetSelect.appendChild(option); }); presetSelect.value = settings.defaultPresetId; updateActions(); });
    }

    function ensureLauncher() { if (!document.body || document.getElementById(launcherId)) return; ensureStyle(); var button = document.createElement('button'); button.id = launcherId; button.type = 'button'; button.title = '语义搜索（Ctrl/⌘ + Shift + K）'; button.setAttribute('aria-label', '打开视频语义搜索'); button.setAttribute('aria-expanded', 'false'); button.innerHTML = '<span class="jf-vs-launcher-icon" aria-hidden="true">⌕</span><span class="jf-vs-launcher-label">语义搜索</span>'; button.addEventListener('click', function () { button.setAttribute('aria-expanded', 'true'); openPanel(); }); document.body.appendChild(button); }
    document.addEventListener('keydown', function (event) { if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'k') { event.preventDefault(); ensureLauncher(); var launcher = document.getElementById(launcherId); if (launcher) launcher.setAttribute('aria-expanded', 'true'); openPanel(); } else if (event.key === 'Escape' && document.getElementById(panelId)) closePanel(); });
    function scheduleEnsure() { if (observerTimer) return; observerTimer = window.setTimeout(function () { observerTimer = 0; ensureLauncher(); }, 180); }
    function attachObserver() { if (observerAttached || !window.MutationObserver) return; var root = document.body || document.documentElement; if (!root) return; observerAttached = true; new MutationObserver(scheduleEnsure).observe(root, { childList: true, subtree: true }); }
    function initialize() { ensureLauncher(); attachObserver(); }
    document.addEventListener('DOMContentLoaded', initialize, false); document.addEventListener('readystatechange', initialize, false); window.addEventListener('load', initialize, false); window.addEventListener('pageshow', initialize, false); if (document.addEventListener) document.addEventListener('visibilitychange', scheduleEnsure, false); initialize(); [0, 250, 800, 1800].forEach(function (delay) { window.setTimeout(initialize, delay); });
})();
