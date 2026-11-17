import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { JSDOM } from 'jsdom';
import SpaceSaverController from '../Jellyfin.Plugin.SpaceSaver/Configuration/configPage.js';

const html = readFileSync(new URL('../Jellyfin.Plugin.SpaceSaver/Configuration/configPage.html', import.meta.url), 'utf8');
const settings = { Backend: 'qsv', Device: '/dev/dri/renderD128', Quality: 24, MinimumSavingsPercent: 5, HardwareDecode: true };
const movie = { Id: 'test-movie', Name: 'Test movie', Path: '/media/Test movie.mkv', Codec: 'h264', Size: 3 * 1024 ** 3, BlockedReason: null };
const response = (items = [movie]) => ({ Items: items, TotalCount: items.length, TotalBytes: items.reduce((sum, item) => sum + item.Size, 0) });

async function until(condition) {
    for (let attempt = 0; attempt < 200; attempt++) {
        if (condition()) return;
        await delay(5);
    }
    assert.fail('Timed out waiting for page activity');
}

function setup(t, options = {}) {
    const dom = new JSDOM(html, { url: 'http://jellyfin.test/web/configurationpage?name=SpaceSaver' });
    const view = dom.window.document.querySelector('#SpaceSaverPage');
    const requests = [];
    let mediaCount = 0;
    const originals = Object.fromEntries(['document', 'window', 'ApiClient'].map(name => [name, globalThis[name]]));
    globalThis.document = dom.window.document;
    globalThis.window = dom.window;
    globalThis.ApiClient = {
        getPluginConfiguration: async () => ({ ...settings }),
        updatePluginConfiguration: async () => ({}),
        getUrl: path => 'http://jellyfin.test/' + path,
        ajax: async request => {
            requests.push(request);
            const url = new URL(request.url);
            if (url.pathname === '/SpaceSaver/Media') return options.media ? options.media(url.searchParams, mediaCount++) : response();
            if (url.pathname === '/SpaceSaver/Jobs') return [];
            if (url.pathname === '/SpaceSaver/Capabilities') return { QsvCompiled: true };
            if (url.pathname === '/SpaceSaver/Jobs/test-movie') return { Id: 'job-id' };
            throw new Error('Unexpected request: ' + request.url);
        }
    };
    // Matches Jellyfin 10.11's view manager: import the module, construct its default export, then show the view.
    new SpaceSaverController(view);
    t.after(() => {
        view.dispatchEvent(new dom.window.Event('viewhide'));
        dom.window.close();
        for (const [name, value] of Object.entries(originals)) {
            if (value === undefined) delete globalThis[name]; else globalThis[name] = value;
        }
    });
    return {
        view, requests,
        element: id => view.querySelector('#' + id),
        show: () => view.dispatchEvent(new dom.window.Event('viewshow')),
        fire: (id, event) => view.querySelector('#' + id).dispatchEvent(new dom.window.Event(event)),
        mediaRequests: () => requests.filter(request => new URL(request.url).pathname === '/SpaceSaver/Media')
    };
}

test('Jellyfin imports the controller without AMD and loads media on viewshow', async t => {
    assert.equal(typeof globalThis.define, 'undefined');
    const page = setup(t);
    page.show();
    await until(() => page.element('ssMedia').children.length === 1);
    assert.match(page.element('ssSummary').textContent, /1 files/);
    assert.match(page.element('ssMedia').textContent, /Test movie/);
    assert.match(page.element('ssMedia').textContent, /3.0 GiB/);
    assert.equal(page.element('ssError').textContent, '');
    await until(() => page.requests.some(request => request.url.endsWith('/Jobs')));
});

test('styled checkbox provides the label span Jellyfin attachedCallback uses', t => {
    const page = setup(t);
    for (const checkbox of page.view.querySelectorAll('input[is="emby-checkbox"]')) {
        const label = checkbox.parentNode;
        assert.equal(label.tagName, 'LABEL');
        const labelText = label.querySelector('span');
        assert.ok(labelText, 'Jellyfin requires a span inside the checkbox label');
        // This is the operation that threw the reported null.classList error.
        labelText.classList.add('checkboxLabel');
    }
});

test('typing searches after a short debounce and clearing restores the full list', async t => {
    const page = setup(t);
    page.show();
    await until(() => page.element('ssMedia').children.length === 1);
    page.element('ssSearch').value = 'Test'; page.fire('ssSearch', 'input');
    page.element('ssSearch').value = 'Test movie'; page.fire('ssSearch', 'input');
    assert.equal(page.mediaRequests().length, 1);
    await until(() => page.mediaRequests().length === 2);
    assert.equal(new URL(page.mediaRequests()[1].url).searchParams.get('search'), 'Test movie');
    page.element('ssSearch').value = ''; page.fire('ssSearch', 'input');
    await until(() => page.mediaRequests().length === 3);
    assert.equal(new URL(page.mediaRequests()[2].url).searchParams.get('search'), '');
});

test('a search entered while media is loading is not discarded', async t => {
    let resolveInitial;
    const initial = new Promise(resolve => { resolveInitial = resolve; });
    const page = setup(t, { media: (query, index) => index === 0 ? initial : response([{ ...movie, Name: query.get('search') }]) });
    page.show();
    await until(() => page.mediaRequests().length === 1);
    page.element('ssSearch').value = 'New search'; page.fire('ssSearch', 'input');
    await delay(350);
    resolveInitial(response());
    await until(() => page.element('ssMedia').textContent.includes('New search'));
    assert.equal(page.mediaRequests().length, 2);
    assert.equal(new URL(page.mediaRequests()[1].url).searchParams.get('search'), 'New search');
});

test('HTTP errors are visible on the page and Refresh retries', async t => {
    const page = setup(t, { media: (_query, index) => {
        if (index === 0) throw new Response(JSON.stringify({ Message: 'Library lookup failed' }), { status: 503, statusText: 'Unavailable' });
        return response();
    } });
    page.show();
    await until(() => page.element('ssError').textContent.includes('Library lookup failed'));
    assert.match(page.element('ssSummary').textContent, /Unable to load/);
    page.fire('ssRefresh', 'click');
    await until(() => page.element('ssMedia').children.length === 1);
    assert.equal(page.element('ssError').textContent, '');
});

test('sort controls issue requests and Convert enqueues the selected item', async t => {
    const page = setup(t);
    page.show();
    await until(() => page.element('ssMedia').children.length === 1);
    page.element('ssDirection').value = 'false'; page.fire('ssDirection', 'change');
    await until(() => page.mediaRequests().length === 2);
    assert.equal(new URL(page.mediaRequests()[1].url).searchParams.get('descending'), 'false');
    page.element('ssMedia').querySelector('button').click();
    await until(() => page.requests.some(request => request.type === 'POST' && request.url.endsWith('/Jobs/test-movie')));
    await until(() => page.element('ssNotice').textContent === 'Queued Test movie');
});

test('hiding the page cancels pending debounced searches', async t => {
    const page = setup(t);
    page.show();
    await until(() => page.element('ssMedia').children.length === 1);
    page.element('ssSearch').value = 'Hidden search'; page.fire('ssSearch', 'input');
    page.view.dispatchEvent(new window.Event('viewhide'));
    await delay(350);
    assert.equal(page.mediaRequests().length, 1);
});
