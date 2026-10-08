/* Customer-service real-time chat over the existing SignalR setup.
   No secrets here. Falls back to the normal form POST when SignalR
   is unavailable, so messaging always works. */
(function () {
    'use strict';

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function init() {
        var root = document.getElementById('support-chat');
        if (!root) return;

        var ticketId = parseInt(root.getAttribute('data-ticket-id'), 10);
        var myId = root.getAttribute('data-current-user-id');
        var isStaff = root.getAttribute('data-is-staff') === 'true';
        var readOnly = root.getAttribute('data-readonly') === 'true';
        var list = document.getElementById('support-messages');
        var form = document.getElementById('support-composer-form');
        var input = document.getElementById('support-message-input');
        var statusBadge = document.getElementById('support-status-badge');
        if (!ticketId || !list || !form || !input) return;

        function scrollDown() {
            list.scrollTop = list.scrollHeight;
        }
        scrollDown();

        function bubble(o) {
            var mine = String(o.senderUserId) === String(myId);
            var div = document.createElement('div');
            div.className = 'support-msg ' + (mine ? 'mine' : 'theirs');
            div.setAttribute('data-message-id', o.messageId);
            div.innerHTML = '<div>' + esc(o.text).replace(/\n/g, '<br>') + '</div>' +
                '<span class="support-meta">' + esc(o.senderName) + ' · ' + esc(o.sentAt) + '</span>';
            return div;
        }

        function noteBubble(o) {
            var div = document.createElement('div');
            div.className = 'support-msg internal-note';
            div.innerHTML = '<span class="internal-tag">Internal note · ' + esc(o.senderName) +
                ' · ' + esc(o.sentAt) + '</span><div>' + esc(o.text).replace(/\n/g, '<br>') + '</div>';
            return div;
        }

        function markRead(connection) {
            if (connection && connection.state === 'Connected') {
                connection.invoke('MarkTicketRead', ticketId).catch(function () { });
            }
        }

        if (!window.signalR || typeof signalR === 'undefined') {
            return; // No SignalR: the form posts normally.
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl('/supporthub')
            .withAutomaticReconnect()
            .build();

        connection.on('SupportMessageReceived', function (o) {
            if (parseInt(o.ticketId, 10) !== ticketId) return;
            if (list.querySelector('[data-message-id="' + o.messageId + '"]')) return;
            list.appendChild(bubble(o));
            scrollDown();
            markRead(connection);
        });

        connection.on('InternalNoteAdded', function (o) {
            if (!isStaff || parseInt(o.ticketId, 10) !== ticketId) return;
            list.appendChild(noteBubble(o));
            scrollDown();
        });

        connection.on('SupportTicketUpdated', function (o) {
            if (parseInt(o.ticketId, 10) !== ticketId || !statusBadge) return;
            statusBadge.textContent = o.status;
        });

        connection.start().then(function () {
            return connection.invoke('JoinTicket', ticketId);
        }).then(function () {
            markRead(connection);
        }).catch(function (err) {
            console.error('Support chat connection error:', err && err.toString());
        });

        // When the mobile keyboard opens, keep the composer in view.
        input.addEventListener('focus', function () {
            setTimeout(function () {
                try { form.scrollIntoView({ block: 'nearest' }); } catch (e) { }
                scrollDown();
            }, 300);
        });

        form.addEventListener('submit', function (ev) {
            if (readOnly) {
                ev.preventDefault();
                return; // Closed tickets are read-only; reopen first.
            }
            if (connection.state !== 'Connected') {
                var btn = form.querySelector('[type="submit"]');
                if (btn) { btn.disabled = true; btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Sending…'; }
                return; // Let the form POST normally (fallback).
            }
            ev.preventDefault();
            var text = input.value.trim();
            if (!text) return;
            input.value = '';
            connection.invoke('SendMessage', ticketId, text).catch(function (err) {
                console.error('Support chat send error:', err && err.toString());
                input.value = text; // Restore so the user can retry / POST.
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
