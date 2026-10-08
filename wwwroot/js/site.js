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
});

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
function initMobileSidebar() {
    var toggle = document.getElementById('sidebarToggle');
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
        setTimeout(function () {
            var close = el.querySelector('.btn-close');
            if (close) { close.click(); } else { el.remove(); }
        }, 6000);
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

    function showToast(type, title, message) {
        var toastContainer = document.getElementById('toast-container');
        if (!toastContainer) {
            toastContainer = document.createElement('div');
            toastContainer.id = 'toast-container';
            toastContainer.className = 'position-fixed bottom-0 end-0 p-3';
            toastContainer.style.zIndex = '1080';
            document.body.appendChild(toastContainer);
        }

        var bgClass = type === 'alert' ? 'bg-danger' :
            type === 'warning' ? 'bg-warning' :
            type === 'success' ? 'bg-success' : 'bg-info';

        var toast = document.createElement('div');
        toast.className = 'toast align-items-center text-bg ' + bgClass + ' border-0';
        toast.setAttribute('role', 'alert');
        toast.innerHTML =
            '<div class="d-flex">' +
            '<div class="toast-body">' +
            '<strong>' + title + '</strong><br/>' + message +
            '</div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button>' +
            '</div>';

        toastContainer.appendChild(toast);
        var bsToast = new bootstrap.Toast(toast, { delay: 8000 });
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
