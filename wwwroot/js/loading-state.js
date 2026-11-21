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

    // Submit-once: a form whose submit button carries data-submit-once is
    // locked after a submit that is really going out (not cancelled by
    // validation or another handler). The button shows data-loading-text
    // (icon kept) and stays disabled until the page is replaced; a server-side
    // validation error re-renders the page, and a back/forward cache restore
    // is undone on pageshow.
    function submitOnceButtons(form) {
        return form.querySelectorAll('button[data-submit-once]');
    }

    function labelTextNode(btn) {
        for (var i = btn.childNodes.length - 1; i >= 0; i--) {
            var n = btn.childNodes[i];
            if (n.nodeType === 3 && n.textContent.trim()) return n;
        }
        return null;
    }

    function setSubmitOnce(btn, active) {
        var text = btn.getAttribute('data-loading-text');
        var node = labelTextNode(btn);
        if (active) {
            if (text && node) { btn.dataset.originalText = node.textContent; node.textContent = text; }
            btn.disabled = true;
            btn.setAttribute('aria-busy', 'true');
        } else {
            if (node && btn.dataset.originalText != null) node.textContent = btn.dataset.originalText;
            delete btn.dataset.originalText;
            btn.disabled = false;
            btn.removeAttribute('aria-busy');
        }
    }

    // Capture phase on document: runs before any form handler, so a second
    // submit (double click, Enter) is stopped before anything else sees it.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement) || !submitOnceButtons(form).length) return;
        if (form.dataset.submitState === 'active') {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);

    // Bubble phase on window: runs after the form's own handlers (jQuery
    // validation included), so a cancelled submit is visible here.
    window.addEventListener('submit', function (e) {
        var form = e.target;
        if (!(form instanceof HTMLFormElement)) return;
        var buttons = submitOnceButtons(form);
        if (!buttons.length || e.defaultPrevented || form.dataset.submitState === 'active') return;
        var $ = window.jQuery;
        if ($ && $.fn && $.fn.valid && $(form).data('validator') && !$(form).valid()) return;
        form.dataset.submitState = 'active';
        buttons.forEach(function (btn) { setSubmitOnce(btn, true); });
    });

    window.addEventListener('pageshow', function () {
        document.querySelectorAll('form[data-submit-state="active"]').forEach(function (form) {
            form.dataset.submitState = '';
            submitOnceButtons(form).forEach(function (btn) { setSubmitOnce(btn, false); });
        });
    });

    window.COMS_LOADING = { setLoading: setLoading, bind: bind };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initWhenReady);
    } else {
        initWhenReady();
    }
})();
