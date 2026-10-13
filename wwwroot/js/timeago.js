// COMS Philippine-time + relative-time helpers (UTC+8, no DST).
// Server stores UTC; this renders PH wall-clock + "5 minutes ago" labels
// and refreshes them in place without reloading the page.
(function () {
    'use strict';
    var PH_OFFSET_MIN = 8 * 60;

    function pad(n) { return (n < 10 ? '0' : '') + n; }

    function toPhDate(d) {
        var ph = new Date(d.getTime() + PH_OFFSET_MIN * 60000);
        return ph;
    }

    function monthName(m) {
        return ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'][m];
    }

    function phAbsolute(utcIso) {
        var d = new Date(utcIso);
        if (isNaN(d)) return '';
        var ph = new Date(d.getTime() + PH_OFFSET_MIN * 60000);
        var h = ph.getUTCHours(), ap = h >= 12 ? 'PM' : 'AM';
        h = h % 12; if (h === 0) h = 12;
        return monthName(ph.getUTCMonth()) + ' ' + pad(ph.getUTCDate()) + ', ' + ph.getUTCFullYear() + ', ' + pad(h) + ':' + pad(ph.getUTCMinutes()) + ' ' + ap;
    }

    function relative(utcIso, nowMs) {
        var d = new Date(utcIso);
        if (isNaN(d)) return '';
        var now = nowMs ? new Date(nowMs) : new Date();
        var s = Math.max(0, Math.floor((now - d) / 1000));
        if (s < 60) return 'Just now';
        var m = Math.floor(s / 60);
        if (m < 60) return m === 1 ? '1 minute ago' : m + ' minutes ago';
        var h = Math.floor(m / 60);
        if (h < 24) return h === 1 ? '1 hour ago' : h + ' hours ago';
        var days = Math.floor(h / 24);
        if (days < 2) {
            // Yesterday (PH calendar day) or 1 day ago — same label.
            return 'Yesterday';
        }
        if (days < 7) return days + ' days ago';
        return phAbsolute(utcIso);
    }

    // Re-render every <time data-utc> on the page.
    function tickTimes() {
        var now = Date.now();
        document.querySelectorAll('time[data-utc]').forEach(function (el) {
            var utc = el.getAttribute('data-utc');
            if (!utc) return;
            el.textContent = relative(utc, now);
            el.setAttribute('title', phAbsolute(utc) + ' (Philippine time)');
            el.setAttribute('datetime', utc);
        });
    }

    window.COMS_TIME = { relative: relative, phAbsolute: phAbsolute, tickTimes: tickTimes };
    document.addEventListener('DOMContentLoaded', function () {
        tickTimes();
        setInterval(tickTimes, 30000);
    });
})();
