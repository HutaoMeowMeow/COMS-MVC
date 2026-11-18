/* COMS — background autosave for "Report a Canal Problem".
   The form itself is server-rendered Razor/Bootstrap. This script only sends
   the current field values to the SaveDraft endpoint and updates a small,
   unobtrusive save-status label. All validation/ownership stays server-side. */
(function () {
    'use strict';

    var form = document.getElementById('reportForm');
    if (!form) return;

    var url = form.getAttribute('data-draft-autosave-url');
    if (!url) return;

    var statusEl = document.getElementById('draftStatus');
    var statusText = document.getElementById('draftStatusText');
    var photoInput = document.getElementById('photoInput');
    var savedPhotoWrap = document.getElementById('draftPhotoSaved');
    var removePhoto = document.getElementById('removeDraftPhoto');
    var saveDraftBtn = document.getElementById('saveDraftBtn');
    var submitBtn = document.getElementById('submitReportBtn');

    var lastSnapshot = '';
    var timer = null;
    var saving = false;
    var submitting = false;
    var savedPhoto = form.getAttribute('data-draft-has-photo') === 'true' ? 'saved' : '';

    var fields = ['ReportType', 'Title', 'Description', 'Location', 'Latitude', 'Longitude', 'CanalId'];

    function value(id) {
        var el = document.getElementById(id);
        return el ? String(el.value || '') : '';
    }

    function snapshot() {
        var parts = fields.map(function (id) { return id + '=' + value(id); });
        var file = photoInput && photoInput.files && photoInput.files[0];
        parts.push('photo=' + (file ? file.name + ':' + file.size : ''));
        parts.push('removePhoto=' + (removePhoto && removePhoto.checked ? '1' : '0'));
        parts.push('savedPhoto=' + savedPhoto);
        return parts.join('|');
    }

    function setStatus(state, text) {
        if (!statusEl) return;
        statusEl.setAttribute('data-state', state);
        if (statusText) statusText.textContent = text;
    }

    function setIcon(state) {
        if (!statusEl) return;
        var icon = statusEl.querySelector('i');
        if (!icon) return;
        icon.className = state === 'saving' ? 'bi bi-arrow-repeat'
            : state === 'error' ? 'bi bi-cloud-slash'
            : state === 'warning' ? 'bi bi-exclamation-circle'
            : 'bi bi-cloud-check';
    }

    function updateSavedPhoto(url) {
        savedPhoto = url ? 'saved' : '';
        if (!savedPhotoWrap) return;
        if (url) {
            var img = savedPhotoWrap.querySelector('img');
            if (img) img.src = url;
            savedPhotoWrap.classList.remove('d-none');
        } else {
            savedPhotoWrap.classList.add('d-none');
            if (removePhoto) removePhoto.checked = false;
        }
    }

    function saveDraft(manual) {
        if (saving || submitting) return;
        if (!manual && snapshot() === lastSnapshot) return;
        saving = true;
        setStatus('saving', manual ? 'Saving draft…' : 'Saving…');
        setIcon('saving');

        var token = form.querySelector('input[name="__RequestVerificationToken"]');
        var data = new FormData();
        if (token) data.append('__RequestVerificationToken', token.value);
        fields.forEach(function (id) {
            var el = document.getElementById(id);
            if (el) data.append(id, el.value);
        });
        if (removePhoto && removePhoto.checked) data.append('removePhoto', 'true');

        var file = photoInput && photoInput.files && photoInput.files[0];
        if (file) data.append('Photo', file);

        fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Requested-With': 'XMLHttpRequest', 'Accept': 'application/json' },
            body: data
        }).then(function (response) {
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.json();
        }).then(function (result) {
            saving = false;
            if (!result || result.success !== true) {
                setStatus('error', 'Unable to save draft. Please try again.');
                setIcon('error');
                return;
            }
            if (file && photoInput) {
                // Persisted server-side: clear the file input so a later
                // submit reuses the saved draft photo instead of sending it twice.
                photoInput.value = '';
                var previewWrap = document.getElementById('photoPreviewWrap');
                if (previewWrap) previewWrap.classList.add('d-none');
            }
            if (typeof result.draftPhotoUrl === 'string' || result.draftPhotoUrl === null) {
                if (result.draftPhotoUrl) {
                    updateSavedPhoto(result.draftPhotoUrl);
                } else if (removePhoto && removePhoto.checked) {
                    updateSavedPhoto(null);
                }
            }
            lastSnapshot = snapshot();
            if (result.warning) {
                setStatus('warning', String(result.warning));
                setIcon('warning');
            } else if (result.empty) {
                setStatus('idle', 'Drafts save automatically while you type.');
                setIcon('idle');
            } else {
                setStatus('saved', 'Draft saved · ' + (result.savedAtPh || 'just now'));
                setIcon('saved');
            }
        }).catch(function () {
            saving = false;
            setStatus('error', 'Unable to save draft — check your connection. Your text stays on this page.');
            setIcon('error');
        });
    }

    function schedule() {
        if (submitting) return;
        if (timer) clearTimeout(timer);
        timer = setTimeout(function () { saveDraft(false); }, 1800);
    }

    fields.forEach(function (id) {
        var el = document.getElementById(id);
        if (el) {
            el.addEventListener('input', schedule);
            el.addEventListener('change', schedule);
        }
    });
    if (photoInput) photoInput.addEventListener('change', schedule);
    if (removePhoto) removePhoto.addEventListener('change', schedule);

    // The map updates the coordinate inputs programmatically (no input event),
    // so a lightweight snapshot poll keeps location changes autosaved.
    lastSnapshot = snapshot();
    setInterval(function () {
        if (submitting) return;
        if (snapshot() !== lastSnapshot && !saving) schedule();
    }, 2500);

    if (saveDraftBtn) {
        saveDraftBtn.addEventListener('click', function (e) {
            if (submitting) return;
            e.preventDefault();
            saveDraft(true);
        });
    }
    form.addEventListener('submit', function (e) {
        var submitter = e.submitter;
        if (submitter && submitter.id === 'saveDraftBtn') {
            // Already handled by the click listener (prevents navigation).
            e.preventDefault();
            return;
        }
        submitting = true;
        if (timer) clearTimeout(timer);
    });

    // Warn once if there are unsaved changes when leaving the page.
    window.addEventListener('beforeunload', function (e) {
        if (submitting) return;
        if (snapshot() === lastSnapshot) return;
        e.preventDefault();
        e.returnValue = '';
    });
})();

