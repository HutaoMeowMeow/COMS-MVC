/* COMS shared form/button loading state.
   One owner for every "disable the submit button while the request runs"
   behaviour. Page scripts opt in with data-loading="submit" (the default:
   disable only on a submit that is actually going out, and always restore
   afterwards) or data-loading="always" (disable even when a later handler
   cancels the submit — used for one-shot actions such as the confirmation
   modal, where the dialog is closing and the button is never reused).

   Why this exists: several pages used to disable their submit button inside
   a submit handler and had no path that re-enabled it. Any handler that ran
   later in the same event and cancelled the submission (client-side
   validation, a duplicate-submit guard, a confirm dialog) left the button
   disabled and spinning forever. The restore below is therefore driven by
   the submit event itself, not by whichever handler happened to run last. */
(function () {
    'use strict';

    var LOADING_TEXT = 'Sending…';
    var unloaded = false;

    // Real navigation signal: the page is being torn down, so a submit button
    // does not need restoring (and a pending restore must be cancelled).
    // beforeunload is the primary signal; pagehide covers browsers where the
    // event is suppressed, and a DOM detach covers SPA-style re-renders.
    window.addEventListener('beforeunload', function () { unloaded = true; });
    window.addEventListener('pagehide', function () { unloaded = true; });

    // Wrap a submit button's text in its own <span class="btn-label"> once, so
    // restoring the original label never depends on saving/restoring
    // innerHTML (fragile, and an injection surface for user-driven markup).
    function ensureLabel(btn) {
        var label = btn.querySelector('.btn-label');
        if (label) return label;
        label = document.createElement('span');
        label.className = 'btn-label';
        label.textContent = btn.textContent.trim();
        btn.textContent = '';
        btn.appendChild(label);
        return label;
    }

    function setLoading(btn, active) {
        if (active) {
            if (btn.dataset.loadingState === 'active') return;
            btn.dataset.loadingState = 'active';
            btn.disabled = true;
            ensureLabel(btn).classList.add('opacity-50');
            var spinnerOn = btn.querySelector('.spinner-border');
            if (spinnerOn) spinnerOn.classList.remove('d-none');
        } else {
            if (btn.dataset.loadingState !== 'active') return;
            btn.dataset.loadingState = '';
            btn.disabled = false;
            var label = btn.querySelector('.btn-label');
            if (label) label.classList.remove('opacity-50');
            var spinnerOff = btn.querySelector('.spinner-border');
            if (spinnerOff) spinnerOff.classList.add('d-none');
        }
    }

    function bind(btn) {
        if (btn.dataset.loadingInit === 'true') return;
        btn.dataset.loadingInit = 'true';

        var always = btn.getAttribute('data-loading') === 'always';
        var form = btn.closest('form');

        if (form) {
            form.addEventListener('submit', function () {
                setLoading(btn, true);
                if (always) return;                 // one-shot: dialog closes
                // The submit event has now propagated through every other
                // listener. If nobody cancelled it the browser is committing
                // to a navigation; if somebody cancelled it the button is
                // still on screen and must be usable again immediately.
                queueMicrotask(function () {
                    if (unloaded || !btn.isConnected) return;
                    setLoading(btn, false);
                });
            });
        } else {
            btn.addEventListener('click', function () {
                setLoading(btn, true);
                if (always) return;
                queueMicrotask(function () {
                    if (unloaded || !btn.isConnected) return;
                    setLoading(btn, false);
                });
            });
        }
    }

    function init() {
        document.querySelectorAll('[data-loading]').forEach(bind);
    }

    // Re-scan once the DOM settles so buttons injected by partials or late
    // scripts are covered too.
    function initWhenReady() {
        init();
        setTimeout(init, 0);
    }

    window.COMS_LOADING = { setLoading: setLoading, bind: bind };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initWhenReady);
    } else {
        initWhenReady();
    }
})();
