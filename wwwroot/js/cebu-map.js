/* COMS Cebu map helper — one owner for the Cebu report-area mask on every
   Leaflet map. The polygons come from the static GeoJSON below; the server
   validates coordinates against the exact same file
   (Services/CebuBoundaryService.cs), so this is presentation + early
   feedback only.

   CebuMap.apply(map, { fit: bool }) -> { isInside(lat, lng | latlng), ready }
   - dims everything outside Cebu, Mactan and Olango (non-interactive mask)
     and draws a white outline around the permitted islands,
   - fit: true fits the view to the islands once loaded,
   - isInside uses the polygons once loaded and the legacy Cebu rectangle
     before that (or if the file cannot be fetched). */
(function () {
    'use strict';

    var OUTSIDE_MESSAGE = 'Location must be inside Cebu. Please select a point within the highlighted area.';

    // Resolve the data file next to this script so an app PathBase still works.
    var script = document.currentScript;
    var DATA_URL = script && script.src
        ? script.src.replace(/js\/cebu-map\.js(\?.*)?$/, 'data/cebu-report-area.geojson')
        : '/data/cebu-report-area.geojson';

    var ringsPromise = null;   // fetched once per page, shared by every map

    function loadRings() {
        if (!ringsPromise) {
            ringsPromise = fetch(DATA_URL, { credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)); })
                .then(function (data) {
                    var rings = [];
                    ((data && data.features) || []).forEach(function (feature) {
                        var geom = feature.geometry;
                        if (!geom) return;
                        var polys = geom.type === 'MultiPolygon' ? geom.coordinates : [geom.coordinates];
                        polys.forEach(function (poly) {
                            var ring = poly && poly[0];
                            if (!ring || ring.length < 3) return;
                            // GeoJSON order is [lng, lat]; Leaflet wants [lat, lng].
                            rings.push(ring.map(function (pt) { return [pt[1], pt[0]]; }));
                        });
                    });
                    if (!rings.length) throw new Error('No Cebu polygons');
                    return rings;
                });
        }
        return ringsPromise;
    }

    function pointInPolygon(lat, lng, ring) {
        var inside = false;
        for (var i = 0, j = ring.length - 1; i < ring.length; j = i++) {
            var latI = ring[i][0], lngI = ring[i][1];
            var latJ = ring[j][0], lngJ = ring[j][1];
            if ((latI > lat) !== (latJ > lat)
                && lng < (lngJ - lngI) * (lat - latI) / (latJ - latI) + lngI) {
                inside = !inside;
            }
        }
        return inside;
    }

    function inRectangle(lat, lng) {
        return lat >= 9.30 && lat <= 11.45 && lng >= 123.20 && lng <= 124.60;
    }

    function apply(map, opts) {
        opts = opts || {};
        var rings = null;

        var ready = loadRings().then(function (loaded) {
            rings = loaded;
            // Negative mask: a world rectangle with every permitted island as
            // a hole, so only land outside the reporting area is dimmed.
            var world = [[85, -180], [85, 180], [-85, 180], [-85, -180]];
            L.polygon([world].concat(rings), {
                stroke: false,
                fillColor: '#1e293b',
                fillOpacity: 0.65,
                interactive: false
            }).addTo(map);
            rings.forEach(function (ring) {
                L.polygon(ring, {
                    color: '#ffffff',
                    weight: 2,
                    opacity: 1,
                    fill: false,
                    interactive: false
                }).addTo(map);
            });
            if (opts.fit) map.fitBounds(L.latLngBounds([].concat.apply([], rings)));
            return rings;
        });
        // Fallback stays on the rectangle; the server keeps enforcing the polygons.
        ready.catch(function () { });

        return {
            ready: ready,
            isInside: function (a, b) {
                var p = L.latLng(a, b);
                if (!p) return false;
                if (!rings) return inRectangle(p.lat, p.lng);
                for (var r = 0; r < rings.length; r++) {
                    if (pointInPolygon(p.lat, p.lng, rings[r])) return true;
                }
                return false;
            }
        };
    }

    window.CebuMap = { apply: apply, OUTSIDE_MESSAGE: OUTSIDE_MESSAGE };
})();
