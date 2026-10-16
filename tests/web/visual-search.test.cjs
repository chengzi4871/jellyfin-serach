const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '../../plugin/Jellyfin.Plugin.VisualSearch/Web/visual-search.js'), 'utf8');

function harness(options = {}) {
    let now = 1000;
    let nextTimer = 1;
    const timers = new Map();
    const elements = new Map();
    const requests = [];
    const listeners = {};
    const classes = () => ({ toggle() {} });
    const status = { textContent: '', classList: classes() };
    const panel = {
        style: {}, attrs: {},
        querySelector: () => status,
        setAttribute(key, value) { this.attrs[key] = value; },
        removeAttribute(key) { delete this.attrs[key]; },
        remove() { elements.delete('jf-visual-search-panel'); }
    };
    const launcher = { style: {}, attrs: {}, setAttribute(key, value) { this.attrs[key] = value; } };
    elements.set('jf-visual-search-panel', panel);
    elements.set('jf-visual-search-launcher', launcher);
    let playerElements = [];
    function schedule(fn, delay, interval = 0) {
        const id = nextTimer++;
        timers.set(id, { fn, at: now + delay, interval });
        return id;
    }
    const window = {
        location: { hash: '#!/home', pathname: '/web/index.html' },
        setTimeout: (fn, delay) => schedule(fn, delay),
        setInterval: (fn, delay) => schedule(fn, delay, delay),
        clearInterval: (id) => timers.delete(id),
        addEventListener() {},
        getComputedStyle: (el) => el.style,
        playbackManager: options.manager
    };
    const ApiClient = {
        serverId: () => 'server-1',
        deviceId: () => 'browser-1',
        getCurrentUserId: () => 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
        isWebSocketOpen: () => options.ws !== false,
        getUrl: (route, params) => 'http://jellyfin.test/' + route + (params ? '?' + new URLSearchParams(params) : ''),
        getJSON: async (url) => {
            requests.push({ method: 'GET', url });
            return options.sessions || [{ Id: 'session-1', DeviceId: 'browser-1', UserId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }];
        },
        ajax: async (request) => {
            requests.push({ method: request.type, url: request.url });
            if (options.reject) throw new Error('HTTP 403 permission denied');
            if (options.onPlay) options.onPlay(window);
        }
    };
    const document = {
        body: {},
        getElementById: (id) => elements.get(id),
        querySelectorAll: () => playerElements,
        addEventListener: (name, fn) => { listeners[name] = fn; }
    };
    class Clock extends Date { static now() { return now; } }
    const instrumented = source.replace(/\}\)\(\);\s*$/, 'window.testApi = {playResults, queueResults, sendSessionPlayback, suspendPanelForPlayback, checkPlaybackRouteReturn, isPlayerVisible, openPanel};\n})();');
    vm.runInNewContext(instrumented, { window, document, ApiClient, Date: Clock, console: { error() {}, warn() {} }, URLSearchParams });
    function advance(milliseconds) {
        const target = now + milliseconds;
        while (true) {
            const due = [...timers].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
            if (!due) break;
            const [id, timer] = due;
            now = timer.at;
            if (timer.interval) timer.at += timer.interval; else timers.delete(id);
            timer.fn();
        }
        now = target;
    }
    return { api: window.testApi, window, document, status, panel, launcher, requests, advance, listeners,
        player: (visible) => {
            playerElements = [{ closest: () => null, style: { display: visible ? 'block' : 'none', visibility: 'visible' }, getClientRects: () => visible ? [{}] : [] }];
        }
    };
}

const item = { ItemId: 'video-1', BestFrame: { TimestampMs: 42000 } };

test('single play sends official self-session command with seek position and keeps results hidden during playback', async () => {
    const h = harness({ onPlay: (win) => { win.location.hash = '#!/video'; } });
    await h.api.playResults([item], item);
    assert.equal(h.requests.length, 2);
    const request = new URL(h.requests[1].url);
    assert.equal(h.requests[1].method, 'POST');
    assert.equal(request.pathname, '/Sessions/session-1/Playing');
    assert.equal(request.searchParams.get('PlayCommand'), 'PlayNow');
    assert.equal(request.searchParams.get('StartPositionTicks'), '420000000');
    assert.equal(request.searchParams.get('ItemIds'), 'video-1');
    h.advance(10000);
    assert.equal(h.panel.style.display, 'none');
    assert.equal(h.launcher.style.display, 'none');
    h.window.location.hash = '#!/home';
    h.advance(1000);
    assert.equal(h.panel.style.display, '');
    assert.equal(h.panel.attrs['aria-hidden'], undefined);
    assert.equal(h.launcher.style.display, '');
});

test('play all preserves result order, deduplicates IDs and starts at zero', async () => {
    const h = harness({ onPlay: (win) => { win.location.hash = '#!/video'; } });
    await h.api.playResults([item, { ItemId: 'video-2' }, item]);
    const request = new URL(h.requests[1].url);
    assert.equal(request.searchParams.get('ItemIds'), 'video-1,video-2');
    assert.equal(request.searchParams.get('StartPositionTicks'), '0');
    assert.equal(request.searchParams.get('StartIndex'), '0');
});

test('queue uses PlayLast and never inserts a seek', async () => {
    const h = harness();
    await h.api.queueResults([item, { ItemId: 'video-2' }]);
    const request = new URL(h.requests[1].url);
    assert.equal(request.searchParams.get('PlayCommand'), 'PlayLast');
    assert.equal(request.searchParams.get('ItemIds'), 'video-1,video-2');
    assert.equal(request.searchParams.has('StartPositionTicks'), false);
});

test('rejected playback restores results with a visible error', async () => {
    const h = harness({ reject: true });
    await h.api.playResults([item], item);
    assert.equal(h.panel.style.display, '');
    assert.match(h.status.textContent, /播放失败.*403/);
});

test('no automatic three/eight-second restoration while playback is still pending', async () => {
    const h = harness();
    const playing = h.api.playResults([item]);
    for (let i = 0; i < 8; i++) await Promise.resolve();
    h.advance(10000);
    assert.equal(h.panel.style.display, 'none');
    h.window.location.hash = '#!/video';
    h.advance(250);
    await playing;
    assert.equal(h.panel.style.display, 'none');
});

test('accepted command without a player produces a visible timeout instead of false success', async () => {
    const h = harness();
    const playing = h.api.playResults([item]);
    for (let i = 0; i < 8; i++) await Promise.resolve();
    h.advance(30250);
    await playing;
    assert.equal(h.panel.style.display, '');
    assert.match(h.status.textContent, /30 秒内播放器未启动/);
    assert.equal(h.requests.filter((x) => x.method === 'POST').length, 1);
});

test('native player visible before route update hides launcher and blocks opening search', () => {
    const h = harness();
    h.player(true);
    h.api.checkPlaybackRouteReturn();
    h.api.openPanel();
    assert.equal(h.launcher.style.display, 'none');
    assert.equal(h.panel.style.display, 'none');
    h.advance(5000);
    assert.equal(h.panel.style.display, 'none');
});

test('cached hidden player does not hide the search launcher', () => {
    const h = harness();
    h.player(false);
    h.api.checkPlaybackRouteReturn();
    assert.equal(h.api.isPlayerVisible(), false);
    assert.equal(h.launcher.style.display, '');
});

test('path router and legacy video OSD routes keep search hidden', () => {
    for (const route of ['#!/video', '#/videoosd.html', '#!/videoplayer']) {
        const h = harness();
        h.window.location.hash = route;
        h.api.checkPlaybackRouteReturn();
        assert.equal(h.launcher.style.display, 'none');
    }
    const h = harness();
    h.window.location.pathname = '/web/video';
    h.api.checkPlaybackRouteReturn();
    assert.equal(h.launcher.style.display, 'none');
});

test('closed websocket or mismatched/ambiguous session never sends a play command to another device', async () => {
    const badSessions = [
        [{ Id: 'wrong-device', DeviceId: 'other-browser', UserId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }],
        [{ Id: 'wrong-user', DeviceId: 'browser-1', UserId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' }],
        [1, 2].map((id) => ({ Id: String(id), DeviceId: 'browser-1', UserId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }))
    ];
    for (const options of [{ ws: false }, ...badSessions.map((sessions) => ({ sessions }))]) {
        const h = harness(options);
        await h.api.playResults([item]);
        assert.equal(h.requests.filter((x) => x.method === 'POST').length, 0);
        assert.equal(h.panel.style.display, '');
        assert.match(h.status.textContent, /播放失败/);
    }
});

test('direct manager rejection does not dispatch a second play command', async () => {
    const h = harness({ manager: { play: async () => { throw new Error('native rejection'); } } });
    await h.api.playResults([item]);
    assert.equal(h.requests.length, 0);
    assert.match(h.status.textContent, /native rejection/);
});

test('sample search chips are absent from the shipped UI', () => {
    assert.doesNotMatch(source, /data-jf-vs-hint|海边户外|红色衣服|家庭聚会|两个人说话/);
});
