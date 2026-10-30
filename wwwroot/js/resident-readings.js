/* COMS Resident Dashboard — latest canal water levels.
   Renders individual water-level gauge cards (one per visible canal) from the
   read-only feed at /SensorReadings/LatestReadingsJson. Only real stored
   readings are rendered; missing/invalid values are omitted, never invented.
   Water-condition classification uses the canal's own Normal/Warning/Critical
   thresholds sourced from the database — no hardcoded danger levels. */
(function () {
    'use strict';

    var ENDPOINT = '/SensorReadings/LatestReadingsJson';
    var REFRESH_MS = 30000;
    var REQUEST_TIMEOUT_MS = 15000;

    var dateTimeFormatter = new Intl.DateTimeFormat('en-PH', {
        timeZone: 'Asia/Manila',
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    });
    var clockFormatter = new Intl.DateTimeFormat('en-PH', {
        timeZone: 'Asia/Manila',
        hour: '2-digit',
        minute: '2-digit'
    });

    function byId(id) { return document.getElementById(id); }
    function toggle(el, hidden) { if (el) el.classList.toggle('d-none', hidden); }

    function parseUtc(value) {
        if (!value) return null;
        var text = String(value);
        var iso = /Z$|[+\-]\d\d:\d\d$/.test(text) ? text : text + 'Z';
        var date = new Date(iso);
        return isNaN(date.getTime()) ? null : date;
    }

    function formatDateTime(date) { return dateTimeFormatter.format(date); }
    function formatClock(date) { return clockFormatter.format(date); }

    // ---- Condition classification (uses database thresholds only) ----

    function hasThresholds(item) {
        return (item.warningLevel > 0 && item.criticalLevel > 0);
    }

    function classify(item) {
        var level = item.level;
        var warning = item.warningLevel || 0;
        var critical = item.criticalLevel || 0;

        if (hasThresholds(item)) {
            if (critical > 0 && level >= critical)
                return { label: 'Critical', cls: 'water-critical', sort: 3 };
            if (warning > 0 && level >= warning)
                return { label: 'Elevated', cls: 'water-elevated', sort: 2 };
            return { label: 'Normal', cls: 'water-normal', sort: 1 };
        }
        // Thresholds not configured on the canal — do not invent a condition.
        return { label: 'Reading', cls: 'water-neutral', sort: 0 };
    }

    function gaugeScaleMax(item) {
        var candidates = [item.level];
        if (item.normalLevel > 0) candidates.push(item.normalLevel);
        if (item.warningLevel > 0) candidates.push(item.warningLevel);
        if (item.criticalLevel > 0) candidates.push(item.criticalLevel);
        var max = Math.max.apply(null, candidates);
        return max > 0 ? max * 1.15 : 1;
    }

    // ---- Normalisation ----

    function normalize(payload) {
        if (!Array.isArray(payload)) throw new Error('BAD_PAYLOAD');

        var byCanal = new Map();
        payload.forEach(function (reading) {
            if (!reading) return;

            // Missing readings must not become zero. Guard the boundary.
            if (reading.waterLevel === null || reading.waterLevel === undefined ||
                reading.waterLevel === '') return;

            var level = Number(reading.waterLevel);
            if (!Number.isFinite(level) || level < 0) return;

            var key = reading.canalId != null
                ? String(reading.canalId)
                : String(reading.canalName || '');
            if (!key) return;

            var date = parseUtc(reading.recordedAt);

            var item = {
                canalId: reading.canalId,
                name: String(reading.canalName || ('Canal ' + reading.canalId)),
                level: level,
                date: date,
                sensorType: reading.sensorType ? String(reading.sensorType) : '',
                normalLevel: Number(reading.normalLevel) || 0,
                warningLevel: Number(reading.warningLevel) || 0,
                criticalLevel: Number(reading.criticalLevel) || 0
            };

            var previous = byCanal.get(key);
            if (previous) {
                if (previous.date && item.date) {
                    if (previous.date >= item.date) return;
                } else if (previous.date && !item.date) {
                    return;
                }
            }
            byCanal.set(key, item);
        });

        var items = Array.from(byCanal.values());
        items.sort(function (a, b) {
            var ca = classify(a), cb = classify(b);
            if (ca.sort !== cb.sort) return cb.sort - ca.sort;
            return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
        });
        return items;
    }

    // ---- Rendering ----

    function esc(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function fmtLevel(value) {
        return Number.isFinite(value) ? value.toFixed(2) : '—';
    }

    function renderGauge(item) {
        var condition = classify(item);
        var max = gaugeScaleMax(item);
        var fillPct = (item.level / max) * 100;
        var showThresholds = hasThresholds(item);

        var card = document.createElement('div');
        card.className = 'water-gauge-card';
        card.setAttribute('role', 'figure');
        card.setAttribute('aria-label',
            item.name + ': water level ' + item.level.toFixed(2) + ' m — ' + condition.label);

        // Header: name + condition badge
        var header = document.createElement('div');
        header.className = 'gauge-header';
        var nameEl = document.createElement('h6');
        nameEl.className = 'gauge-name';
        nameEl.textContent = item.name;
        header.appendChild(nameEl);
        var badge = document.createElement('span');
        badge.className = 'condition-badge ' + condition.cls;
        badge.textContent = condition.label;
        header.appendChild(badge);
        card.appendChild(header);

        // Gauge body: track + fill + value + markers
        var body = document.createElement('div');
        body.className = 'gauge-body';
        body.setAttribute('role', 'img');
        body.setAttribute('aria-label', 'Water level gauge showing ' + item.level.toFixed(2) + ' meters');

        // Threshold band markers (shown as dashed lines on the track)
        if (showThresholds) {
            body.appendChild(createThresholdMarker(item.normalLevel, max, 'Normal'));
            body.appendChild(createThresholdMarker(item.warningLevel, max, 'Elevated'));
            body.appendChild(createThresholdMarker(item.criticalLevel, max, 'Critical'));
        }

        var fill = document.createElement('div');
        fill.className = 'gauge-fill ' + condition.cls;
        fill.style.height = Math.max(fillPct, 4) + '%';
        fill.setAttribute('aria-hidden', 'true');
        body.appendChild(fill);

        var valueEl = document.createElement('div');
        valueEl.className = 'gauge-value';
        valueEl.textContent = item.level.toFixed(2) + ' m';
        body.appendChild(valueEl);

        // Scale labels (min / max)
        var minLabel = document.createElement('div');
        minLabel.className = 'gauge-scale-label gauge-scale-min';
        minLabel.textContent = '0 m';
        body.appendChild(minLabel);

        var maxLabel = document.createElement('div');
        maxLabel.className = 'gauge-scale-label gauge-scale-max';
        maxLabel.textContent = Math.round(max) + ' m';
        body.appendChild(maxLabel);

        card.appendChild(body);

        // Footer: timestamp + sensor type
        var footer = document.createElement('div');
        footer.className = 'gauge-footer';
        var when = document.createElement('small');
        when.className = 'gauge-time text-muted';
        if (item.date) {
            var time = document.createElement('time');
            time.setAttribute('data-utc', item.date.toISOString());
            time.setAttribute('title', formatDateTime(item.date) + ' (Philippine time)');
            time.textContent = 'Updated ' + formatClock(item.date);
            when.appendChild(time);
        } else {
            when.textContent = 'Reading time unavailable';
        }
        footer.appendChild(when);
        if (item.sensorType) {
            var type = document.createElement('small');
            type.className = 'gauge-sensor text-muted ms-2';
            type.textContent = '· ' + item.sensorType;
            footer.appendChild(type);
        }
        card.appendChild(footer);

        return card;
    }

    function createThresholdMarker(level, scaleMax, label) {
        if (!level || level <= 0 || level > scaleMax) return document.createDocumentFragment();
        var marker = document.createElement('div');
        marker.className = 'gauge-marker';
        marker.style.bottom = ((level / scaleMax) * 100) + '%';
        marker.setAttribute('title', label + ' threshold: ' + level.toFixed(2) + ' m');
        marker.setAttribute('aria-hidden', 'true');
        return marker;
    }

    function renderGauges(items) {
        var grid = byId('waterGaugeGrid');
        if (!grid) return;
        grid.innerHTML = '';
        var fragment = document.createDocumentFragment();
        items.forEach(function (item) { fragment.appendChild(renderGauge(item)); });
        grid.appendChild(fragment);
    }

    function renderLegend(items) {
        var container = byId('waterGaugeLegend');
        if (!container) return;

        var anyThresholds = items.some(function (item) { return hasThresholds(item); });
        if (!anyThresholds) {
            container.classList.add('d-none');
            return;
        }
        container.classList.remove('d-none');
        container.innerHTML = '';

        var entries = [
            { label: 'Normal', cls: 'water-normal' },
            { label: 'Elevated', cls: 'water-elevated' },
            { label: 'Critical', cls: 'water-critical' }
        ];

        entries.forEach(function (entry) {
            var item = document.createElement('span');
            item.className = 'legend-item';
            var swatch = document.createElement('span');
            swatch.className = 'legend-swatch ' + entry.cls;
            item.appendChild(swatch);
            var text = document.createElement('span');
            text.className = 'legend-text';
            text.textContent = entry.label;
            item.appendChild(text);
            container.appendChild(item);
        });
    }

    function init() {
        var skeleton = byId('chartSkeleton');
        var errorBox = byId('chartError');
        var errorMessage = byId('chartErrorMessage');
        var empty = byId('chartEmpty');
        var content = byId('chartContent');
        var status = byId('residentChartStatus');
        var retry = byId('chartRetry');

        if (!content) return;

        var requestInFlight = false;
        var lastSuccess = null;

        function setStatus(text) { if (status) status.textContent = text; }

        function showLoading() {
            toggle(skeleton, false);
            toggle(errorBox, true);
            toggle(empty, true);
            toggle(content, true);
            setStatus('Loading latest readings…');
        }

        function showEmpty() {
            toggle(skeleton, true);
            toggle(errorBox, true);
            toggle(empty, false);
            toggle(content, true);
            setStatus('No readings available');
        }

        function showSuccess(items) {
            toggle(skeleton, true);
            toggle(errorBox, true);
            toggle(empty, true);
            toggle(content, false);
            setStatus('Updated ' + formatClock(new Date()) + ' · refreshes every 30s');
            renderGauges(items);
            renderLegend(items);
        }

        function messageFor(reason) {
            switch (reason) {
                case 'AUTH':
                    return 'Your session may have expired or you do not have access to canal readings. Please sign in again.';
                case 'NOT_FOUND':
                    return 'The canal readings endpoint was not found. Please contact the administrator.';
                case 'TIMEOUT':
                    return 'The request timed out. Check your connection and try again.';
                case 'BAD_PAYLOAD':
                    return 'The server returned an unexpected response. Please try again.';
                default:
                    return 'Could not load canal water levels. Please try again.';
            }
        }

        function showFailure(reason) {
            var message = messageFor(reason);
            var hasRows = lastSuccess !== null && lastSuccess.length > 0;
            toggle(skeleton, true);
            toggle(errorBox, false);
            if (errorMessage) errorMessage.textContent = hasRows
                ? message + ' Showing the last successful update.'
                : message;
            toggle(content, !hasRows);
            toggle(empty, !(lastSuccess !== null && lastSuccess.length === 0));
            setStatus(hasRows ? 'Update failed · showing last readings' : 'Unable to load readings');
        }

        function load(initial) {
            if (requestInFlight) return;
            requestInFlight = true;
            if (initial) showLoading();
            else setStatus('Refreshing readings…');

            var controller = new AbortController();
            var timedOut = false;
            var timer = setTimeout(function () {
                timedOut = true;
                controller.abort();
            }, REQUEST_TIMEOUT_MS);

            fetch(ENDPOINT, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { 'Accept': 'application/json' },
                signal: controller.signal
            })
                .then(function (response) {
                    if (!response.ok) {
                        if (response.status === 401 || response.status === 403) throw new Error('AUTH');
                        if (response.status === 404) throw new Error('NOT_FOUND');
                        throw new Error('HTTP_' + response.status);
                    }
                    var contentType = response.headers.get('content-type') || '';
                    if (contentType.indexOf('application/json') === -1) throw new Error('AUTH');
                    return response.json();
                })
                .then(function (payload) {
                    var items = normalize(payload);
                    lastSuccess = items;
                    if (items.length === 0) {
                        showEmpty();
                        return;
                    }
                    showSuccess(items);
                })
                .catch(function (error) {
                    if (error && error.name === 'AbortError' && !timedOut) return;
                    showFailure(timedOut ? 'TIMEOUT' : (error && error.message));
                })
                .finally(function () {
                    clearTimeout(timer);
                    requestInFlight = false;
                });
        }

        if (retry) {
            retry.addEventListener('click', function () {
                if (requestInFlight) return;
                load(true);
            });
        }

        // Keep gauges themed when the user flips light/dark mode.
        if (window.MutationObserver) {
            new MutationObserver(function () {
                // Re-render so CSS variables pick up new colors immediately.
                if (lastSuccess && lastSuccess.length > 0) renderLegend(lastSuccess);
            }).observe(document.documentElement, {
                attributes: true,
                attributeFilter: ['data-theme', 'data-bs-theme']
            });
        }

        load(true);
        setInterval(function () {
            if (!document.hidden) load(false);
        }, REFRESH_MS);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
