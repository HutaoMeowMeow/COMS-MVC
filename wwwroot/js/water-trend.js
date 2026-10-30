/* COMS dashboard Water Level Trend.
   Renders only real stored readings returned by
   /SensorReadings/WaterLevelTrendJson. One line per visible canal, with the
   server-side barangay scope already applied. No sample/random data. */
(function () {
    'use strict';

    var REFRESH_MS = 30000;
    var HOURS = 5;
    var MAX_POINTS_PER_CANAL = 300;
    var PH_OFFSET_MS = 8 * 60 * 60 * 1000;
    var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
        'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var PALETTE = ['#4dabf7', '#20c997', '#ffd43b', '#ff8787',
        '#b197fc', '#63e6be', '#ffa94d', '#74c0fc', '#f783ac', '#a9e34b'];

    function init() {
        var canvas = document.getElementById('waterLevelTrendChart');
        if (!canvas || typeof Chart === 'undefined') return;

        var loading = document.getElementById('waterTrendLoading');
        var error = document.getElementById('waterTrendError');
        var empty = document.getElementById('waterTrendEmpty');
        var note = document.getElementById('waterTrendNote');

        var chart = null;
        var requestInFlight = false;
        var lastPayload = null;

        function toggle(el, hidden) {
            if (el) el.classList.toggle('d-none', hidden);
        }

        function theme() {
            var styles = getComputedStyle(document.documentElement);
            var isDark = document.documentElement.getAttribute('data-theme') === 'dark';
            var text = (styles.getPropertyValue('--theme-text-secondary') || '').trim()
                || (isDark ? '#b8b8b8' : '#5b6b7f');
            return {
                isDark: isDark,
                text: text,
                grid: isDark ? 'rgba(255,255,255,.14)' : 'rgba(16,42,67,.10)',
                tooltipBg: isDark ? '#242424' : '#1e3a5f'
            };
        }

        function parseUtc(value) {
            if (!value) return null;
            var iso = /Z$|[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z';
            var date = new Date(iso);
            return isNaN(date.getTime()) ? null : date;
        }

        function phDateLabel(ms) {
            var ph = new Date(Number(ms) + PH_OFFSET_MS);
            return MONTHS[ph.getUTCMonth()] + ' ' + ph.getUTCDate() + ', ' + ph.getUTCFullYear() +
                ' · ' + String(ph.getUTCHours()).padStart(2, '0') + ':' +
                String(ph.getUTCMinutes()).padStart(2, '0');
        }

        function phTickLabel(ms) {
            var ph = new Date(Number(ms) + PH_OFFSET_MS);
            return String(ph.getUTCHours()).padStart(2, '0') + ':' +
                String(ph.getUTCMinutes()).padStart(2, '0');
        }

        function destroyChart() {
            if (chart) {
                chart.destroy();
                chart = null;
            }
        }

        function buildDatasets(series) {
            var datasets = [];
            (series || []).forEach(function (canal, index) {
                var points = (canal.readings || [])
                    .map(function (reading) {
                        var date = parseUtc(reading.recordedAt);
                        var level = Number(reading.waterLevel);
                        if (!date || !Number.isFinite(level)) return null;
                        return { x: date.getTime(), y: level };
                    })
                    .filter(Boolean)
                    .sort(function (a, b) { return a.x - b.x; });

                if (points.length === 0) return;

                var color = PALETTE[index % PALETTE.length];
                datasets.push({
                    label: canal.canalName || ('Canal ' + (canal.canalId || '')),
                    data: points,
                    borderColor: color,
                    backgroundColor: color,
                    borderWidth: 2,
                    tension: 0.25,
                    fill: false,
                    spanGaps: true,
                    pointRadius: points.length === 1 ? 4 : 2,
                    pointHoverRadius: 6,
                    pointHitRadius: 12,
                    pointBackgroundColor: color,
                    pointBorderColor: color
                });
            });
            return datasets;
        }

        function createChart(datasets) {
            destroyChart();
            var t = theme();
            canvas.classList.remove('d-none');

            chart = new Chart(canvas.getContext('2d'), {
                type: 'line',
                data: { datasets: datasets },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    normalized: true,
                    interaction: { mode: 'nearest', intersect: false },
                    scales: {
                        x: {
                            type: 'linear',
                            ticks: {
                                color: t.text,
                                autoSkip: true,
                                maxTicksLimit: 8,
                                maxRotation: 0,
                                callback: function (value) { return phTickLabel(value); }
                            },
                            grid: { color: t.grid, drawTicks: false },
                            border: { color: t.grid },
                            title: { display: true, text: 'Time (Asia/Manila)', color: t.text }
                        },
                        y: {
                            beginAtZero: false,
                            ticks: { color: t.text },
                            grid: { color: t.grid },
                            border: { color: t.grid },
                            title: { display: true, text: 'Water Level (m)', color: t.text }
                        }
                    },
                    plugins: {
                        legend: {
                            display: datasets.length > 1,
                            labels: { color: t.text, boxWidth: 12, boxHeight: 12, usePointStyle: true }
                        },
                        tooltip: {
                            backgroundColor: t.tooltipBg,
                            titleColor: '#ffffff',
                            bodyColor: '#ffffff',
                            callbacks: {
                                title: function (items) {
                                    return items.length ? phDateLabel(items[0].parsed.x) : '';
                                },
                                label: function (context) {
                                    return (context.dataset.label || 'Water level') + ': ' +
                                        Number(context.parsed.y).toFixed(2) + ' m';
                                }
                            }
                        }
                    }
                }
            });
        }

        function totalPoints(datasets) {
            return datasets.reduce(function (sum, dataset) {
                return sum + dataset.data.length;
            }, 0);
        }

        function render(payload) {
            lastPayload = payload;
            var series = payload && Array.isArray(payload.canals) ? payload.canals : [];
            var datasets = buildDatasets(series);
            var points = totalPoints(datasets);

            toggle(loading, true);
            toggle(error, true);

            if (points === 0) {
                destroyChart();
                canvas.classList.add('d-none');
                toggle(empty, false);
                toggle(note, true);
                return;
            }

            toggle(empty, true);
            createChart(datasets);

            // A single point is real data, but it cannot form a trend line.
            var hasLine = datasets.some(function (dataset) { return dataset.data.length >= 2; });
            if (!hasLine) {
                note.textContent = points === 1
                    ? 'Only one valid water-level reading is available for this period. A trend line will appear once at least two readings have been recorded.'
                    : 'Each canal currently has only one valid reading. A trend line needs at least two readings per canal.';
                toggle(note, false);
            } else {
                toggle(note, true);
            }
        }

        function fail() {
            destroyChart();
            canvas.classList.add('d-none');
            if (note) note.textContent = '';
            toggle(note, true);
            toggle(empty, true);
            toggle(error, false);
            toggle(loading, true);
        }

        function load() {
            if (requestInFlight) return;
            requestInFlight = true;
            toggle(loading, false);
            toggle(error, true);

            fetch('/SensorReadings/WaterLevelTrendJson?hours=' + HOURS + '&maxPoints=' + MAX_POINTS_PER_CANAL, {
                method: 'GET',
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(function (response) {
                    if (!response.ok) throw new Error('Water-level trend request failed');
                    return response.json();
                })
                .then(render)
                .catch(fail)
                .finally(function () {
                    requestInFlight = false;
                    toggle(loading, true);
                });
        }

        // Keep the chart readable when the user flips light/dark mode.
        if (window.MutationObserver) {
            new MutationObserver(function () {
                if (lastPayload) render(lastPayload);
            }).observe(document.documentElement, {
                attributes: true,
                attributeFilter: ['data-theme', 'data-bs-theme']
            });
        }

        load();
        setInterval(function () {
            if (!document.hidden) load();
        }, REFRESH_MS);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
