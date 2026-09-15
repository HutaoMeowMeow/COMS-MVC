document.addEventListener('DOMContentLoaded', function () {
    if (window.isAuthenticated) {
        initializeSignalR();
    } else {
        console.log('User not authenticated, skipping SignalR.');
    }
    updateActiveNav();
});

function updateActiveNav() {
    var currentPath = window.location.pathname;
    var navLinks = document.querySelectorAll('.sidebar-nav .nav-link');
    navLinks.forEach(function (link) {
        var href = link.getAttribute('href');
        if (href && currentPath === href) {
            link.classList.add('active');
        }
    });
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

        toast.addEventListener('hidden.bs.toast', function () {
            toast.remove();
        });
    }
}
