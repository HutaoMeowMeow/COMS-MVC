/* Canal Monitoring page (Canals/Map). Read-only: no click-to-place, no dragging.
   The list/detail panel works without the map, so a map failure never hides data. */
(function () {
    'use strict';

    var STATUS = {
        normal: { icon: 'bi-check-circle-fill', fill: '#16a34a' },
        warning: { icon: 'bi-exclamation-triangle-fill', fill: '#d97706' },
        critical: { icon: 'bi-exclamation-octagon-fill', fill: '#dc2626' },
        inactive: { icon: 'bi-pause-circle-fill', fill: '#64748b' },
        unknown: { icon: 'bi-question-circle-fill', fill: '#64748b' }
    };
    var ALERT_RING = '#1d4ed8';
    var SELECTED_STROKE = '#0b2a4a';

    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function statusKey(status) {
        var k = String(status || '').trim().toLowerCase();
        return STATUS[k] ? k : 'unknown';
    }

    function badge(status) {
        var k = statusKey(status);
        return '<span class="cm-badge cm-st-' + k + '"><i class="bi ' + STATUS[k].icon + '" aria-hidden="true"></i>' +
            esc(status || 'Unknown') + '</span>';
    }

    function level(v) {
        return typeof v === 'number' && isFinite(v) ? esc(v.toFixed(2)) + ' m' : '&ndash;';
    }

    function plural(n, word) { return n + ' ' + word + (n === 1 ? '' : 's'); }

    function hasLocation(c) {
        return isFinite(c.latitude) && isFinite(c.longitude) && !(c.latitude === 0 && c.longitude === 0);
    }

    document.addEventListener('DOMContentLoaded', function () {
        var root = document.getElementById('canalMonitor');
        var dataEl = document.getElementById('canalMonitorData');
        if (!root || !dataEl) return;

        var canals = [];
        try { canals = JSON.parse(dataEl.textContent) || []; } catch (e) { canals = []; }

        var detailsUrl = root.getAttribute('data-details-url') || '/Canals/Details';
        var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        var listView = document.getElementById('cmListView');
        var detailView = document.getElementById('cmDetailView');
        var detail = document.getElementById('cmDetail');
        var list = document.getElementById('cmList');
        var search = document.getElementById('cmSearch');
        var noMatch = document.getElementById('cmNoMatch');
        var live = document.getElementById('cmLive');
        var backBtn = document.getElementById('cmBack');
        var overlay = document.getElementById('cmMapOverlay');

        var byId = {};
        canals.forEach(function (c) { byId[c.canalId] = c; });

        // ---- List ----
        if (list) {
            list.innerHTML = canals.map(function (c) {
                var alerts = c.activeAlertCount > 0
                    ? '<span class="cm-alert-chip"><i class="bi bi-bell-fill" aria-hidden="true"></i>' + esc(plural(c.activeAlertCount, 'alert')) + '</span>'
                    : '';
                return '<li><button type="button" class="cm-item" data-id="' + esc(c.canalId) + '">' +
                    '<span class="cm-item-main"><span class="cm-item-name">' + esc(c.canalName) + '</span>' +
                    '<span class="cm-item-meta">' + esc(c.barangay) + '</span></span>' +
                    '<span class="cm-item-side">' + badge(c.status) + alerts + '</span>' +
                    '</button></li>';
            }).join('');

            list.addEventListener('click', function (e) {
                var btn = e.target.closest('.cm-item');
                if (btn) select(Number(btn.getAttribute('data-id')));
            });
        }

        if (search) {
            search.addEventListener('input', function () {
                var q = search.value.trim().toLowerCase();
                var shown = 0;
                Array.prototype.forEach.call(list.querySelectorAll('.cm-item'), function (btn) {
                    var c = byId[btn.getAttribute('data-id')];
                    var hay = [c.canalName, c.barangay, c.city, c.location, c.status].join(' ').toLowerCase();
                    var match = !q || hay.indexOf(q) !== -1;
                    btn.parentNode.hidden = !match;
                    if (match) shown++;
                });
                noMatch.hidden = shown > 0;
                live.textContent = shown + ' of ' + plural(canals.length, 'canal') + ' shown.';
            });
        }

        // ---- Detail ----
        var selectedId = null;

        function renderDetail(c) {
            var reading = c.latestWaterLevel == null
                ? '<p class="cm-muted mb-0">No readings recorded yet</p>'
                : '<p class="cm-reading mb-1"><span class="cm-reading-value">' + level(c.latestWaterLevel) + '</span></p>' +
                  '<p class="cm-muted small mb-0">Recorded ' +
                  (c.latestReadingAt ? '<time datetime="' + esc(c.latestReadingAt) + '">' + esc(c.latestReadingText || c.latestReadingAt) + '</time>' : 'at an unknown time') +
                  '</p>';

            var sensors = c.sensors && c.sensors.length
                ? '<ul class="cm-sensors">' + c.sensors.map(function (s) {
                    var online = String(s.status || '').toLowerCase() === 'online';
                    return '<li><span><span class="cm-sensor-code">' + esc(s.sensorCode) + '</span>' +
                        '<span class="cm-muted small d-block">' + esc(s.sensorType) + '</span></span>' +
                        '<span class="cm-badge ' + (online ? 'cm-st-normal' : 'cm-st-inactive') + '">' +
                        '<i class="bi ' + (online ? 'bi-wifi' : 'bi-wifi-off') + '" aria-hidden="true"></i>' + esc(s.status || 'Unknown') + '</span></li>';
                }).join('') + '</ul>'
                : '<p class="cm-muted mb-0">No sensors installed</p>';

            var place = [c.barangay, c.city].filter(Boolean).join(', ');

            detail.innerHTML =
                '<h3 id="cmDetailTitle" class="cm-detail-title" tabindex="-1">' + esc(c.canalName) + '</h3>' +
                '<div class="mb-2">' + badge(c.status) + '</div>' +
                '<p class="cm-muted small mb-0"><i class="bi bi-geo-alt me-1" aria-hidden="true"></i>' + esc(c.location) + '</p>' +
                (place ? '<p class="cm-muted small mb-0">' + esc(place) + '</p>' : '') +
                (hasLocation(c) ? '' : '<p class="cm-muted small mb-0">No map location recorded.</p>') +

                '<section class="cm-block" aria-labelledby="cmLevelTitle">' +
                '<h4 id="cmLevelTitle" class="cm-block-title">Latest water level</h4>' + reading +
                '<dl class="cm-thresholds">' +
                '<div><dt>Normal</dt><dd>' + level(c.normalWaterLevel) + '</dd></div>' +
                '<div><dt>Warning</dt><dd>' + level(c.warningWaterLevel) + '</dd></div>' +
                '<div><dt>Critical</dt><dd>' + level(c.criticalWaterLevel) + '</dd></div>' +
                '</dl></section>' +

                '<section class="cm-block" aria-labelledby="cmAlertTitle">' +
                '<h4 id="cmAlertTitle" class="cm-block-title">Active alerts</h4>' +
                '<p class="mb-0">' + (c.activeAlertCount > 0
                    ? '<span class="cm-alert-chip"><i class="bi bi-bell-fill" aria-hidden="true"></i>' + esc(plural(c.activeAlertCount, 'active alert')) + '</span>'
                    : '<span class="cm-muted">No active alerts</span>') + '</p></section>' +

                '<section class="cm-block" aria-labelledby="cmSensorTitle">' +
                '<h4 id="cmSensorTitle" class="cm-block-title">Sensors <span class="cm-muted fw-normal">(' +
                esc(c.sensorOnline) + ' of ' + esc(c.sensorTotal) + ' online)</span></h4>' + sensors + '</section>' +

                '<a class="btn btn-primary cm-btn w-100 mt-3" href="' + esc(detailsUrl + '/' + encodeURIComponent(c.canalId)) + '">' +
                'View canal details<i class="bi bi-arrow-right ms-1" aria-hidden="true"></i></a>';
        }

        function select(id) {
            var c = byId[id];
            if (!c) return;
            selectedId = id;
            renderDetail(c);
            listView.hidden = true;
            detailView.hidden = false;
            live.textContent = c.canalName + ' selected.';
            highlight(id);
            var title = document.getElementById('cmDetailTitle');
            if (title) title.focus({ preventScroll: window.innerWidth >= 992 });
        }

        function showList() {
            var prev = selectedId;
            selectedId = null;
            highlight(null);
            detailView.hidden = true;
            listView.hidden = false;
            live.textContent = 'Showing canal list.';
            var btn = list && list.querySelector('.cm-item[data-id="' + prev + '"]');
            if (btn && !btn.parentNode.hidden) btn.focus(); else if (search) search.focus();
        }

        if (backBtn) backBtn.addEventListener('click', showList);

        // ---- Map ----
        var map = null;
        var markers = {};

        function markerStyle(c, selected) {
            return {
                radius: selected ? 12 : 8,
                color: selected ? SELECTED_STROKE : '#ffffff',
                weight: selected ? 4 : 2,
                fillColor: STATUS[statusKey(c.status)].fill,
                fillOpacity: 1
            };
        }

        function highlight(id) {
            Object.keys(markers).forEach(function (key) {
                var m = markers[key];
                var on = String(key) === String(id);
                m.dot.setStyle(markerStyle(m.canal, on));
                if (m.ring) m.ring.setRadius(on ? 17 : 13);
                if (on) {
                    if (m.ring) m.ring.bringToFront();
                    m.dot.bringToFront();
                    map.setView(m.dot.getLatLng(), Math.max(map.getZoom(), 14), { animate: !reduceMotion });
                }
            });
        }

        function showMapError() {
            overlay.hidden = false;
            overlay.setAttribute('role', 'alert');
            overlay.classList.add('cm-map-error');
            overlay.innerHTML = '<i class="bi bi-exclamation-circle me-2" aria-hidden="true"></i>' +
                '<span>The map could not be loaded. The canal list is still available.</span>';
        }

        if (typeof L === 'undefined' || typeof CebuMap === 'undefined') {
            showMapError();
            return;
        }

        try {
            var cebuBounds = L.latLngBounds([9.30, 123.20], [11.45, 124.60]);
            map = L.map('monitoringMap', {
                maxBounds: cebuBounds,
                maxBoundsViscosity: 1.0,
                minZoom: 9,
                maxZoom: 18
            }).setView([10.3157, 123.8854], 10);
            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
            }).addTo(map);

            var cebu = CebuMap.apply(map, { fit: true });
            // Hide the overlay either way; on fetch failure the rectangle bounds still apply.
            cebu.ready.then(hideOverlay, hideOverlay);

            canals.forEach(function (c) {
                if (!hasLocation(c)) return;
                var ll = [c.latitude, c.longitude];
                var ring = c.activeAlertCount > 0
                    ? L.circleMarker(ll, { radius: 13, color: ALERT_RING, weight: 3, fill: false, interactive: false }).addTo(map)
                    : null;
                var dot = L.circleMarker(ll, markerStyle(c, false)).addTo(map);
                dot.bindTooltip(esc(c.canalName) + ' &middot; ' + esc(c.status || 'Unknown') +
                    (c.activeAlertCount > 0 ? ' &middot; ' + esc(plural(c.activeAlertCount, 'alert')) : ''));
                dot.on('click', function () { select(c.canalId); });
                markers[c.canalId] = { dot: dot, ring: ring, canal: c };
            });
        } catch (e) {
            map = null;
            markers = {};
            showMapError();
        }

        function hideOverlay() { if (!overlay.classList.contains('cm-map-error')) overlay.hidden = true; }
    });
})();
