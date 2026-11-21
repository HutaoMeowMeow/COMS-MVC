/* COMS Notifications — per-notification delete and mark-as-read via AJAX
   (delete with confirmation, toast feedback, immediate DOM update). Falls
   back to standard form POST when JS is unavailable (the form submits
   normally). */
(function () {
    'use strict';

    var isAjaxDelete = function (form) {
        return form && form.getAttribute('data-ajax-delete') === 'true';
    };

    var getToken = function (form) {
        var tokenInput = form && form.querySelector('input[name="__RequestVerificationToken"]');
        return tokenInput ? tokenInput.value : null;
    };

    var findItem = function (form) {
        return form ? form.closest('.list-group-item') : null;
    };

    var updateUnreadCount = function (count) {
        var badge = document.getElementById('notification-count-badge');
        if (badge) {
            if (count > 0) {
                badge.textContent = count;
                badge.style.display = 'inline-block';
            } else {
                badge.style.display = 'none';
            }
        }
        var pill = document.getElementById('notifications-unread-pill');
        if (pill) {
            pill.textContent = count + ' unread';
            pill.classList.toggle('d-none', count <= 0);
        }
        if (window.refreshNotificationPreview) window.refreshNotificationPreview();
    };

    var postForm = function (form, token) {
        var formData = new FormData();
        formData.append('__RequestVerificationToken', token);
        return fetch(form.getAttribute('action'), {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'X-Requested-With': 'XMLHttpRequest',
                'Accept': 'application/json'
            },
            body: formData
        }).then(function (response) {
            return response.json();
        });
    };

    // Turn an unread row into the same look the server renders for read rows.
    var markItemRead = function (item, form) {
        if (item) {
            item.classList.remove('is-unread');
            var title = item.querySelector('.notification-title');
            if (title) {
                title.classList.remove('fw-bold');
                title.classList.add('text-muted');
            }
            var badge = item.querySelector('[data-read-badge]');
            if (badge) {
                badge.classList.remove('bg-primary-subtle', 'text-primary');
                badge.classList.add('bg-secondary-subtle', 'text-secondary');
                badge.textContent = 'Read';
            }
            // The focused button is about to disappear; keep keyboard users in the row.
            var next = item.querySelector('form[data-ajax-delete="true"] button');
            if (next && form.contains(document.activeElement)) next.focus({ preventScroll: true });
        }
        form.remove();
    };

    var handleMarkRead = function (form) {
        if (form.dataset.busy === 'true') return;
        var token = getToken(form);
        if (!form.getAttribute('action') || !token) {
            showToast('error', 'Update failed', 'Missing security token. Please refresh the page.');
            return;
        }
        var button = form.querySelector('button[type="submit"]');
        var setBusy = function (busy) {
            form.dataset.busy = busy ? 'true' : '';
            if (button) button.disabled = busy;
        };
        setBusy(true);

        postForm(form, token)
            .then(function (data) {
                if (data && data.success) {
                    markItemRead(findItem(form), form);
                    if (typeof data.unreadCount === 'number') {
                        updateUnreadCount(data.unreadCount);
                    }
                } else {
                    setBusy(false);
                    showToast('error', 'Update failed', (data && data.message) || 'Could not mark the notification as read.');
                }
            })
            .catch(function () {
                setBusy(false);
                showToast('error', 'Connection error', 'Could not mark the notification as read. Please try again.');
            });
    };

    var showToast = function (type, title, message) {
        var container = document.getElementById('toast-container');
        if (!container) {
            container = document.createElement('div');
            container.id = 'toast-container';
            container.className = 'position-fixed bottom-0 end-0 p-3';
            container.setAttribute('role', 'region');
            container.setAttribute('aria-label', 'Notifications');
            container.setAttribute('aria-live', 'polite');
            document.body.appendChild(container);
        }

        var bgClass = type === 'error' ? 'bg-danger' : 'bg-success';
        var icon = type === 'error' ? 'bi-exclamation-octagon-fill' : 'bi-check-circle-fill';
        var delay = type === 'error' ? 12000 : 5000;

        var toast = document.createElement('div');
        toast.className = 'toast align-items-center text-bg ' + bgClass + ' border-0';
        toast.setAttribute('role', 'alert');
        toast.setAttribute('data-bs-delay', String(delay));

        toast.innerHTML =
            '<div class="d-flex">' +
            '<div class="toast-body">' +
            '<i class="bi ' + icon + ' me-1" aria-hidden="true"></i>' +
            '<strong>' + title + '</strong><br/>' + message +
            '</div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>' +
            '</div>';

        container.appendChild(toast);
        var bsToast = new bootstrap.Toast(toast, { delay: delay, autohide: true });
        bsToast.show();

        toast.addEventListener('hidden.bs.toast', function () {
            toast.remove();
        });
    };

    function init() {
        var content = document.querySelector('.list-group.list-group-flush');
        if (!content) return;

        content.addEventListener('submit', function (e) {
            var readForm = e.target.closest('form[data-ajax-read="true"]');
            if (readForm) {
                e.preventDefault();
                handleMarkRead(readForm);
                return;
            }

            var form = e.target.closest('form[data-ajax-delete="true"]');
            if (!form) return;

            e.preventDefault();

            var item = findItem(form);
            var action = form.getAttribute('action');
            var token = getToken(form);

            if (!action || !token) {
                showToast('error', 'Delete failed', 'Missing security token. Please refresh the page.');
                return;
            }

            var confirmed = window.confirm('Delete this notification? This cannot be undone.');
            if (!confirmed) return;

            postForm(form, token)
                .then(function (data) {
                    if (data.success) {
                        if (item) {
                            item.style.transition = 'opacity .3s ease, transform .3s ease';
                            item.style.opacity = '0';
                            item.style.transform = 'scale(.98)';
                            setTimeout(function () {
                                if (item.parentNode) item.remove();
                            }, 300);
                        }
                        if (typeof data.unreadCount === 'number') {
                            updateUnreadCount(data.unreadCount);
                        }
                        showToast('success', 'Deleted', 'Notification removed.');
                    } else {
                        showToast('error', 'Delete failed', data.message || 'Could not delete the notification.');
                    }
                })
                .catch(function () {
                    showToast('error', 'Connection error', 'Could not delete the notification. Please try again.');
                });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
