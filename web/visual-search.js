(function () {
    'use strict';
    if (window.JellyfinVisualSearch && window.JellyfinVisualSearch.initialized) return;
    window.JellyfinVisualSearch = {
        search: function (query, libraryIds, limit) {
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('VisualSearch/Search'),
                data: JSON.stringify({ query: query, libraryIds: libraryIds || [], limit: limit || 30 }),
                contentType: 'application/json'
            });
        },
        health: function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/Health')); },
        initialized: true
    };

    function addButton() {
        if (document.getElementById('visualSearchButton')) return;
        var input = document.querySelector('#searchText, input[is="emby-search"]');
        if (!input || !input.parentElement) return;
        var button = document.createElement('button');
        button.id = 'visualSearchButton'; button.type = 'button'; button.title = '视频语义搜索';
        button.textContent = '🧠'; button.className = 'button-flat paper-icon-button';
        button.style.marginLeft = '4px';
        button.addEventListener('click', async function () {
            var query = window.prompt('输入视频内容，例如：海边玩水、卧室两个人');
            if (!query) return;
            button.disabled = true;
            try {
                var data = await window.JellyfinVisualSearch.search(query, [], 30);
                showResults(data);
            } catch (e) {
                window.alert('语义搜索失败：' + (e.message || e));
            } finally { button.disabled = false; }
        });
        input.parentElement.appendChild(button);
    }

    function showResults(data) {
        var old = document.getElementById('visualSearchResults'); if (old) old.remove();
        var box = document.createElement('div'); box.id = 'visualSearchResults';
        box.style.cssText = 'position:fixed;z-index:9999;right:2em;top:5em;width:28em;max-height:70vh;overflow:auto;background:var(--theme-background);padding:1em;box-shadow:0 4px 20px #0008';
        var close = document.createElement('button'); close.textContent = '关闭'; close.onclick = function () { box.remove(); };
        box.appendChild(close);
        var title = document.createElement('h3'); title.textContent = '语义搜索：' + data.query; box.appendChild(title);
        (data.results || []).forEach(function (item) {
            var a = document.createElement('a'); a.href = '#!/details?id=' + encodeURIComponent(item.itemId); a.textContent = (item.title || item.itemId) + '  (' + Number(item.score).toFixed(3) + ')';
            a.style.display = 'block'; a.style.padding = '.5em 0'; box.appendChild(a);
        });
        document.body.appendChild(box);
    }

    var observer = new MutationObserver(addButton);
    observer.observe(document.body, { childList: true, subtree: true });
    addButton();
})();
