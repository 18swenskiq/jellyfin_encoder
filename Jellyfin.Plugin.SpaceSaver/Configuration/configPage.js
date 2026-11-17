// Jellyfin 10.11 imports plugin controllers as ES modules and constructs the default export.
export default function SpaceSaverController(view) {
    var pluginId = 'e6f799fe-5693-4b68-95af-a7010949adeb';
    var start = 0, limit = 100, total = 0, timer = null, searchTimer = null, active = false, generation = 0, loadingMedia = false, mediaPending = false, mediaGeneration = 0;
    var $ = function (id) { return view.querySelector('#' + id); };
    var loading = {
        show: function () { view.setAttribute('aria-busy', 'true'); },
        hide: function () { view.setAttribute('aria-busy', 'false'); }
    };
    function toast(message) { $('ssNotice').textContent = message; }
    function normalize(value) {
        if (Array.isArray(value)) return value.map(normalize);
        if (value && typeof value === 'object') {
            var result = {};
            Object.keys(value).forEach(function (key) { result[key.charAt(0).toLowerCase() + key.slice(1)] = normalize(value[key]); });
            return result;
        }
        return value;
    }
    async function api(path, method) {
        try {
            return normalize(await ApiClient.ajax({ type: method || 'GET', url: ApiClient.getUrl('SpaceSaver/' + path), dataType: method === 'DELETE' || /\/Cancel$/.test(path) ? undefined : 'json' }));
        } catch (error) {
            var message = error.responseText || error.statusText || error.message || 'Request failed';
            if (typeof error.text === 'function') {
                try { message = await error.text() || message; } catch (_) { /* Keep the HTTP error when its body cannot be read. */ }
            }
            try { var body = JSON.parse(message); message = body.Message || body.message || body.title || message; } catch (_) { /* Plain error text. */ }
            throw new Error(message);
        }
    }
    function report(error) { $('ssError').textContent = error.message || String(error); }
    function bytes(size) {
        if (!size) return '0 B';
        var power = Math.min(4, Math.floor(Math.log(size) / Math.log(1024)));
        return (size / Math.pow(1024, power)).toFixed(1) + ' ' + ['B', 'KiB', 'MiB', 'GiB', 'TiB'][power];
    }
    function element(tag, text) { var e = document.createElement(tag); if (text !== undefined) e.textContent = text; return e; }
    function button(text, action) {
        var b = element('button', text); b.type = 'button'; b.className = 'raised emby-button';
        b.addEventListener('click', async function () { b.disabled = true; try { await action(); } catch (error) { report(error); } finally { b.disabled = false; } });
        return b;
    }
    async function loadMedia() {
        var request = ++mediaGeneration;
        if (loadingMedia) { mediaPending = true; return; }
        loadingMedia = true;
        $('ssSummary').textContent = 'Loading media…';
        try {
            var query = new URLSearchParams({ start: start, limit: limit, sort: $('ssSort').value, descending: $('ssDirection').value, search: $('ssSearch').value });
            var data = await api('Media?' + query.toString());
            if (!active || request !== mediaGeneration) return;
            total = data.totalCount;
            $('ssSummary').textContent = total + ' files · ' + bytes(data.totalBytes) + ' known source size';
            $('ssMedia').replaceChildren(); $('ssAll').checked = false;
            data.items.forEach(function (item) {
                var row = element('tr'); row.style.borderBottom = '1px solid #7775';
                var checkCell = element('td'), check = element('input'); check.type = 'checkbox'; check.value = item.id; check.disabled = Boolean(item.blockedReason); check.setAttribute('aria-label', 'Select ' + item.name); checkCell.append(check);
                var nameCell = element('td'); nameCell.style.padding = '1em'; nameCell.append(element('strong', item.name));
                var path = element('div', item.path); path.style.cssText = 'font-size:.85em;opacity:.7;overflow-wrap:anywhere'; nameCell.append(path);
                var action = element('td');
                if (item.blockedReason) action.textContent = item.blockedReason;
                else action.append(button('Convert', async function () { await api('Jobs/' + item.id, 'POST'); toast('Queued ' + item.name); await loadJobs(); }));
                row.append(checkCell, nameCell, element('td', item.codec), element('td', item.size > 0 ? bytes(item.size) : 'Unknown'), action); $('ssMedia').append(row);
            });
            $('ssPrevious').disabled = start === 0; $('ssNext').disabled = start + limit >= total;
            $('ssPage').textContent = total ? (start + 1) + '–' + Math.min(start + limit, total) + ' of ' + total : 'No matching files';
        } catch (error) {
            if (active && request === mediaGeneration) {
                $('ssSummary').textContent = 'Unable to load media. See the error above and try Refresh list.';
                throw error;
            }
        } finally {
            loadingMedia = false;
            if (mediaPending && active) { mediaPending = false; loadMedia().catch(report); }
        }
    }
    async function loadJobs() {
        var jobs = await api('Jobs');
        if (!active) return;
        $('ssJobs').replaceChildren();
        if (!jobs.length) $('ssJobs').textContent = 'No conversions yet.';
        jobs.forEach(function (job) {
            var card = element('div'); card.style.cssText = 'border:1px solid #7775;border-radius:8px;padding:1em;margin-bottom:1em';
            card.append(element('strong', job.name + ' · ' + job.state));
            if (['Encoding', 'Validating', 'Committing'].includes(job.state)) {
                var progress = element('progress'); progress.max = 100; progress.value = job.progress; progress.style.cssText = 'width:100%;margin-top:1em'; card.append(progress);
            }
            card.append(element('p', job.message));
            if (job.outputBytes) card.append(element('p', bytes(job.originalBytes) + ' → ' + bytes(job.outputBytes) + ' · ' + bytes(Math.max(0, job.originalBytes - job.outputBytes)) + (job.backupDeleted ? ' reclaimed' : ' potential savings')));
            if (['Queued', 'Encoding', 'Validating'].includes(job.state)) card.append(button('Cancel', async function () { await api('Jobs/' + job.id + '/Cancel', 'POST'); await loadJobs(); }));
            if (job.state === 'Completed' && !job.backupDeleted) card.append(button('Delete original backup', async function () {
                if (!window.confirm('Permanently delete the original for “' + job.name + '”? Review the converted file first. This cannot be undone.')) return;
                await api('Jobs/' + job.id + '/Backup', 'DELETE'); await loadJobs();
            }));
            var paths = element('details'); paths.append(element('summary', 'File paths and recovery information'));
            var info = element('pre', 'Source: ' + job.source + '\nOutput: ' + job.output + '\nOriginal backup: ' + job.backup + '\nTemporary: ' + job.temporary); info.style.cssText = 'white-space:pre-wrap;overflow-wrap:anywhere'; paths.append(info); card.append(paths);
            $('ssJobs').append(card);
        });
    }
    async function poll(epoch) {
        if (!active || epoch !== generation) return;
        try { await loadJobs(); } catch (error) { report(error); }
        if (active && epoch === generation) timer = setTimeout(function () { poll(epoch); }, 3000);
    }
    $('ssSettings').addEventListener('submit', async function (event) {
        event.preventDefault(); loading.show();
        try {
            var config = await ApiClient.getPluginConfiguration(pluginId);
            config.Backend = $('ssBackend').value; config.Device = $('ssDevice').value.trim(); config.Quality = Number($('ssQuality').value);
            config.MinimumSavingsPercent = Number($('ssSavings').value); config.HardwareDecode = $('ssDecode').checked;
            await ApiClient.updatePluginConfiguration(pluginId, config); toast('Settings saved; queued jobs keep their original settings.');
        } catch (error) { report(error); } finally { loading.hide(); }
    });
    $('ssTest').addEventListener('click', async function () {
        $('ssTest').disabled = true;
        try { $('ssCapabilities').textContent = 'Testing GPU…'; var result = await api('TestGpu', 'POST'); $('ssCapabilities').textContent = result.message; }
        catch (error) { $('ssCapabilities').textContent = error.message; } finally { $('ssTest').disabled = false; }
    });
    function refresh() { clearTimeout(searchTimer); start = 0; $('ssError').textContent = ''; loadMedia().catch(report); }
    $('ssRefresh').addEventListener('click', refresh);
    $('ssSort').addEventListener('change', refresh); $('ssDirection').addEventListener('change', refresh);
    $('ssSearch').addEventListener('input', function () {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(refresh, 300);
    });
    $('ssSearch').addEventListener('keydown', function (event) { if (event.key === 'Enter') { event.preventDefault(); refresh(); } });
    $('ssPrevious').addEventListener('click', function () { if (loadingMedia) return; start = Math.max(0, start - limit); loadMedia().catch(report); });
    $('ssNext').addEventListener('click', function () { if (loadingMedia) return; start += limit; loadMedia().catch(report); });
    $('ssAll').addEventListener('change', function () { view.querySelectorAll('#ssMedia input:not(:disabled)').forEach(function (check) { check.checked = $('ssAll').checked; }); });
    $('ssQueue').addEventListener('click', async function () {
        $('ssQueue').disabled = true; $('ssError').textContent = '';
        try {
            var selected = Array.from(view.querySelectorAll('#ssMedia input:checked:not(:disabled)'));
            var errors = [], count = 0;
            for (var check of selected) { try { await api('Jobs/' + check.value, 'POST'); check.checked = false; count++; } catch (error) { errors.push(error.message); } }
            toast(count + ' files queued'); if (errors.length) report(new Error(errors.join('\n'))); await loadJobs();
        } finally { $('ssQueue').disabled = false; }
    });
    view.addEventListener('viewshow', async function () {
        active = true; var epoch = ++generation; loading.show();
        try {
            var config = normalize(await ApiClient.getPluginConfiguration(pluginId));
            if (!active || epoch !== generation) return;
            $('ssBackend').value = config.backend; $('ssDevice').value = config.device; $('ssQuality').value = config.quality;
            $('ssSavings').value = config.minimumSavingsPercent; $('ssDecode').checked = config.hardwareDecode;
            await loadMedia();
            if (!active || epoch !== generation) return;
            var capabilities = await api('Capabilities'); $('ssCapabilities').textContent = JSON.stringify(capabilities, null, 2);
        } catch (error) { report(error); } finally { loading.hide(); poll(epoch); }
    });
    view.addEventListener('viewhide', function () { active = false; generation++; mediaGeneration++; mediaPending = false; clearTimeout(timer); clearTimeout(searchTimer); });
}
