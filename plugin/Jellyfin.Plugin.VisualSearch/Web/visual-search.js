(function () {
    'use strict';
    if (window.JellyfinVisualSearch) return;
    window.JellyfinVisualSearch = {
        search: function (query, libraryIds, limit) {
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('VisualSearch/Search'),
                data: JSON.stringify({ query: query, libraryIds: libraryIds || [], limit: limit || 30 }),
                contentType: 'application/json'
            });
        },
        health: function () { return ApiClient.getJSON(ApiClient.getUrl('VisualSearch/Health')); }
    };
})();
