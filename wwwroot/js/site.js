document.addEventListener('DOMContentLoaded', function () {
    if (window.isAuthenticated) {
        initializeSignalR();
    } else {
        console.log('User not authenticated, skipping SignalR.');
    }
    updateActiveNav();
    autoDismissAlerts();
    initScrollReveal();
    initMobileSidebar();
    initLoadingButtons();
    initCharCounters();
});

// Character counters: markup owns the "/max characters" suffix —
//   <div class="form-text"><span data-char-count-for="Field">0</span>/150 characters</div>
// so JS updates ONLY the number inside the span. Setting the full
// "n/max characters" string on the span duplicates the suffix
// ("0/2000 characters/2000 characters"). Runs once per input.
function initCharCounters() {
    document.querySelectorAll('[data-char-counter]').forEach(function (input) {
        if (input.dataset.counterBound === 'true') return;
        input.dataset.counterBound = 'true';
        var max = parseInt(input.getAttribute('data-char-counter'), 10);
        var name = input.getAttribute('name');
        var shortName = name ? name.split('.').pop() : null;
        var counter = shortName ? document.querySelector('[data-char-count-for="' + shortName + '"]') : null;
        if (!counter) {
            var wrap = document.createElement('div');
            wrap.className = 'form-text';
            counter = document.createElement('span');
            counter.textContent = '0';
            wrap.appendChild(counter);
            wrap.appendChild(document.createTextNode('/' + max + ' characters'));
            input.insertAdjacentElement('afterend', wrap);
            counter = wrap.querySelector('span');
        }
        // Enforce maxlength even if markup omits it (server still validates).
        if (max && !input.getAttribute('maxlength')) input.setAttribute('maxlength', String(max));
        function update() {
            var len = (input.value || '').length;
            counter.textContent = String(len);
            var over = !!(max && len >= max);
            counter.classList.toggle('text-danger', over);
            var formText = counter.closest('.form-text');
            if (formText) formText.classList.toggle('text-danger', over);
        }
        input.addEventListener('input', update);
        update();
    });
}

// Subtle loading state: prevents accidental double submission on
// forms/buttons marked with [data-loading]. No new framework.
function initLoadingButtons() {
    document.querySelectorAll('[data-loading]').forEach(function (btn) {
        var form = btn.closest('form');
        if (form) {
            form.addEventListener('submit', function () {
                if (btn.disabled) return;
                btn.disabled = true;
                btn.dataset.originalHtml = btn.innerHTML;
                btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1" aria-hidden="true"></span>Sending…';
            });
        } else {
            btn.addEventListener('click', function () {
                btn.disabled = true;
            });
        }
    });
}

// Mobile sidebar: the hamburger toggles the existing .sidebar.show mechanism.
// Closes on nav tap, backdrop tap, Escape, and resize to desktop.
function initMobileSidebar() {    var toggle = document.getElementById('sidebarToggle');
    var sidebar = document.getElementById('sidebarNav') || document.querySelector('.sidebar');
    var backdrop = document.getElementById('sidebarBackdrop');
    if (!toggle || !sidebar) return;

    function open() {
        sidebar.classList.add('show');
        if (backdrop) backdrop.classList.add('visible');
        toggle.setAttribute('aria-expanded', 'true');
        toggle.setAttribute('aria-label', 'Close navigation');
    }

    function close() {
        sidebar.classList.remove('show');
        if (backdrop) backdrop.classList.remove('visible');
        toggle.setAttribute('aria-expanded', 'false');
        toggle.setAttribute('aria-label', 'Open navigation');
    }

    toggle.addEventListener('click', function () {
        if (sidebar.classList.contains('show')) { close(); } else { open(); }
    });
    if (backdrop) backdrop.addEventListener('click', close);
    sidebar.querySelectorAll('.nav-link').forEach(function (link) {
        link.addEventListener('click', function () {
            if (window.innerWidth < 992) close();
        });
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') close();
    });
    window.addEventListener('resize', function () {
        if (window.innerWidth >= 992) close();
    });
}

// Scroll reveal: below-fold sections fade up when scrolled into view.
// Purely presentational — .scroll-reveal has a CSS fallback that shows
// content even if this observer never runs.
function initScrollReveal() {
    var els = document.querySelectorAll('.scroll-reveal');
    if (!els.length) return;
    if (!('IntersectionObserver' in window)) {
        els.forEach(function (el) { el.classList.add('in-view'); });
        return;
    }
    var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
            if (entry.isIntersecting) {
                entry.target.classList.add('in-view');
                observer.unobserve(entry.target);
            }
        });
    }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
    els.forEach(function (el) { observer.observe(el); });
}

// Presentation only: fade TempData alerts out after a few seconds.
// The messages, types, and manual close buttons are unchanged.
function autoDismissAlerts() {
    var alerts = document.querySelectorAll('.content-area > .alert.alert-dismissible');
    alerts.forEach(function (el) {
        var close = el.querySelector('.btn-close');
        if (close) {
            var delay = el.classList.contains('alert-warning') ? 15000 : 8000;
            setTimeout(function () {
                close.click();
            }, delay);
        }
    });
}

function updateActiveNav() {
    var currentPath = window.location.pathname;
    var navLinks = document.querySelectorAll('.sidebar-nav .nav-link');
    var best = null, bestLen = -1;
    navLinks.forEach(function (link) {
        var href = link.getAttribute('href');
        if (!href || href === '#') return;
        if (currentPath === href || (href !== '/' && currentPath.toLowerCase().startsWith(href.toLowerCase()))) {
            if (href.length > bestLen) { best = link; bestLen = href.length; }
        }
    });
    if (best) {
        navLinks.forEach(function (l) { l.classList.remove('active'); l.removeAttribute('aria-current'); });
        best.classList.add('active');
        best.setAttribute('aria-current', 'page');
    }
}

function initializeSignalR() {
    if (!window.isAuthenticated) {
        console.log('SignalR skipped: user not authenticated.');
        return;
    }
    if (!window.signalR || typeof signalR === 'undefined') {
        console.log('SignalR not available, skipping real-time updates.');
        return;
    }

    var connection = new signalR.HubConnectionBuilder()
        .withUrl('/monitoringhub')
        .configureLogging(signalR.LogLevel.Information)
        .build();

    connection.start().then(function () {
        console.log('SignalR connected.');
        updateNotificationCount();
    }).catch(function (err) {
        console.error('SignalR connection error:', err.toString());
    });

    connection.on('SensorReadingAdded', function (data) {
        if (window.location.pathname.includes('/SensorReadings') ||
            window.location.pathname.includes('/Dashboard')) {
            console.log('New sensor reading:', data);
            updateLatestReadings(data);
        }
    });

    connection.on('AlertCreated', function (alert) {
        console.log('New alert:', alert);
        showToast('alert', '[' + alert.severity + '] ' + alert.title, alert.description);
        updateNotificationCount();
    });

    connection.on('AlertUpdated', function (data) {
        console.log('Alert updated:', data);
        showToast('info', 'Alert Updated', 'Alert status has been updated.');
        updateNotificationCount();
    });

    connection.on('NotificationReceived', function (notification) {
        // Dedupe: SignalR may replay after reconnect; ignore known IDs.
        window.__seenNotifIds = window.__seenNotifIds || new Set();
        if (notification && notification.notificationId) {
            if (window.__seenNotifIds.has(notification.notificationId)) return;
            window.__seenNotifIds.add(notification.notificationId);
        }
        updateNotificationCount();
        if (window.refreshNotificationPreview) window.refreshNotificationPreview();
    });

    connection.on('NotificationCountUpdated', function (userId) {
        updateNotificationCount();
    });

    connection.on('RiskAssessmentUpdated', function (data) {
        console.log('Risk assessment updated:', data);
    });

    function updateLatestReadings(reading) {
        var table = document.getElementById('readingsTable');
        if (table) {
            var tbody = table.querySelector('tbody');
            if (tbody) {
                var row = tbody.querySelector('tr');
                if (row && row.cells.length >= 7) {
                    row.cells[2].textContent = reading.waterLevel + ' m';
                    row.cells[3].textContent = reading.flowRate;
                    row.cells[4].textContent = reading.debrisLevel + '%';
                    row.cells[5].textContent = reading.turbidity;
                    row.cells[6].textContent = reading.temperature + '°C';
                }
            }
        }
    }

    function updateNotificationCount() {
        $.get('/Notifications/UnreadCount', function (data) {
            var badge = document.getElementById('notification-count-badge');
            if (badge) {
                if (data.count > 0) {
                    badge.textContent = data.count;
                    badge.style.display = 'inline-block';
                } else {
                    badge.style.display = 'none';
                }
            }
        }).fail(function () {
            var badge = document.getElementById('notification-count-badge');
            if (badge) badge.style.display = 'none';
        });
    }

    // Toast policy:
    //  - at most 3 visible at once; an arriving 4th fades out the oldest,
    //  - identical (type+title+message) toasts never stack — the existing
    //    one is refreshed instead,
    //  - success/info auto-dismiss in 5s, warning in 8s, error/alert in 12s
    //    (all remain manually dismissible via the close button),
    //  - the container is pointer-events:none (only toast bodies capture
    //    clicks) so it never blocks the page underneath.
    var TOAST_MAX_VISIBLE = 3;
    var TOAST_DELAYS = { success: 5000, info: 5000, warning: 8000, alert: 12000, error: 12000 };
    var TOAST_ICONS = {
        success: 'bi-check-circle-fill',
        info: 'bi-info-circle-fill',
        warning: 'bi-exclamation-triangle-fill',
        alert: 'bi-exclamation-octagon-fill',
        error: 'bi-exclamation-octagon-fill'
    };

    function escHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function getToastContainer() {
        var container = document.getElementById('toast-container');
        if (!container) {
            // Single shared container; layout, spacing, stacking, z-index,
            // and the mobile adaptation live in #toast-container CSS.
            container = document.createElement('div');
            container.id = 'toast-container';
            container.className = 'position-fixed bottom-0 end-0 p-3';
            container.setAttribute('role', 'region');
            container.setAttribute('aria-label', 'Notifications');
            container.setAttribute('aria-live', 'polite');
            document.body.appendChild(container);
        }
        return container;
    }

    function showToast(type, title, message) {
        var toastContainer = getToastContainer();
        var key = type + '|' + title + '|' + message;

        // Dedupe: an identical visible toast just gets its timer refreshed.
        var existing = toastContainer.querySelector('[data-toast-key="' + CSS.escape(key) + '"]');
        if (existing) {
            var existingInstance = bootstrap.Toast.getInstance(existing);
            if (existingInstance) {
                existing.classList.remove('show');
                void existing.offsetWidth;
                existing.classList.add('show');
                existingInstance.show();
                return;
            }
        }

        // Cap the stack: fade out the oldest toast gracefully.
        while (toastContainer.children.length >= TOAST_MAX_VISIBLE) {
            var oldest = toastContainer.firstElementChild;
            var oldestInstance = bootstrap.Toast.getInstance(oldest);
            if (oldestInstance) {
                oldestInstance.hide();
            } else {
                oldest.remove();
            }
            // Only strip one per arrival; the rest fade on their own timers.
            break;
        }

        var bgClass = type === 'alert' || type === 'error' ? 'bg-danger' :
            type === 'warning' ? 'bg-warning' :
            type === 'success' ? 'bg-success' : 'bg-info';
        var icon = TOAST_ICONS[type] || TOAST_ICONS.info;
        var delay = TOAST_DELAYS[type] || TOAST_DELAYS.info;

        var toast = document.createElement('div');
        toast.className = 'toast align-items-center text-bg ' + bgClass + ' border-0';
        toast.setAttribute('role', 'alert');
        toast.setAttribute('data-toast-key', key);
        toast.innerHTML =
            '<div class="d-flex">' +
            '<div class="toast-body">' +
            '<i class="bi ' + icon + ' me-1" aria-hidden="true"></i>' +
            '<strong>' + escHtml(title) + '</strong><br/>' + escHtml(message) +
            '</div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close notification"></button>' +
            '</div>';

        toastContainer.appendChild(toast);
        var bsToast = new bootstrap.Toast(toast, { delay: delay, autohide: true });
        bsToast.show();

        // Ring the navbar bell once so the new notification is noticed.
        var bell = document.querySelector('.notification-bell .bi-bell');
        if (bell) {
            bell.classList.remove('bell-ring');
            void bell.offsetWidth;
            bell.classList.add('bell-ring');
        }

        toast.addEventListener('hidden.bs.toast', function () {
            toast.remove();
        });
    }
}
