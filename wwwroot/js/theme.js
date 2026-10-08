/* COMS theme toggle: light/dark mode with localStorage persistence.
   Stores only the non-sensitive "coms-theme" value ("light" | "dark").
   Works independently of authentication; no server calls. */
(function () {
    'use strict';

    var STORAGE_KEY = 'coms-theme';
    var LIGHT = 'light';
    var DARK = 'dark';

    function getStoredTheme() {
        try {
            var value = window.localStorage.getItem(STORAGE_KEY);
            return value === LIGHT || value === DARK ? value : null;
        } catch (e) {
            return null;
        }
    }

    function getSystemTheme() {
        if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            return DARK;
        }
        return LIGHT;
    }

    function resolveInitialTheme() {
        return getStoredTheme() || getSystemTheme();
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        // Bootstrap 5.3 native dark mode for dropdowns, modals, tables, forms.
        document.documentElement.setAttribute('data-bs-theme', theme);
        syncToggles(theme);
    }

    function setStoredTheme(theme) {
        try {
            window.localStorage.setItem(STORAGE_KEY, theme);
        } catch (e) {
            /* Private mode etc.: theme still applies for this page load. */
        }
    }

    function syncToggles(theme) {
        var isDark = theme === DARK;
        document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
            btn.setAttribute('aria-pressed', isDark ? 'true' : 'false');
            btn.setAttribute('aria-label', isDark ? 'Switch to light mode' : 'Switch to dark mode');
            btn.setAttribute('title', isDark ? 'Switch to light mode' : 'Switch to dark mode');
            var icon = btn.querySelector('[data-theme-toggle-icon]');
            if (icon) {
                icon.classList.toggle('bi-moon-fill', !isDark);
                icon.classList.toggle('bi-sun-fill', isDark);
            }
            var label = btn.querySelector('[data-theme-toggle-text]');
            if (label) {
                label.textContent = isDark ? 'Light' : 'Dark';
            }
        });
    }

    function toggleTheme() {
        var current = document.documentElement.getAttribute('data-theme') === DARK ? DARK : LIGHT;
        var next = current === DARK ? LIGHT : DARK;
        setStoredTheme(next);
        applyTheme(next);
    }

    function init() {
        // Anonymous pages (Login, ...) are forced light by the server flag.
        // localStorage is left untouched so the theme restores after login.
        if (window.COMS_FORCE_LIGHT_THEME === true) {
            applyTheme(LIGHT);
            return;
        }

        applyTheme(resolveInitialTheme());

        document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
            btn.addEventListener('click', toggleTheme);
        });

        // Follow OS changes only until the user makes an explicit choice.
        if (window.matchMedia) {
            var mq = window.matchMedia('(prefers-color-scheme: dark)');
            var onChange = function (e) {
                if (!getStoredTheme()) {
                    applyTheme(e.matches ? DARK : LIGHT);
                }
            };
            if (typeof mq.addEventListener === 'function') {
                mq.addEventListener('change', onChange);
            } else if (typeof mq.addListener === 'function') {
                mq.addListener(onChange);
            }
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
