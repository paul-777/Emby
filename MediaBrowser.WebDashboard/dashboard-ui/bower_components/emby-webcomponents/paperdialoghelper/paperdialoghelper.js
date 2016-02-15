﻿define(['historyManager', 'focusManager', 'performanceManager', 'browser', 'layoutManager', 'paper-dialog', 'scale-up-animation', 'fade-out-animation', 'fade-in-animation', 'css!./paperdialoghelper.css'], function (historyManager, focusManager, performanceManager, browser, layoutManager) {

    function paperDialogHashHandler(dlg, hash, resolve) {

        var self = this;
        self.originalUrl = window.location.href;
        var activeElement = document.activeElement;
        var removeScrollLockOnClose = false;

        function onHashChange(e) {

            var isBack = self.originalUrl == window.location.href;

            if (isBack || !dlg.opened) {
                window.removeEventListener('popstate', onHashChange);
            }

            if (isBack) {
                self.closedByBack = true;
                dlg.close();
            }
        }

        function onDialogClosed() {

            if (removeScrollLockOnClose) {
                document.body.classList.remove('noScroll');
            }

            window.removeEventListener('popstate', onHashChange);

            if (!self.closedByBack && isHistoryEnabled(dlg)) {
                var state = history.state || {};
                if (state.dialogId == hash) {
                    history.back();
                }
            }

            activeElement.focus();

            if (dlg.getAttribute('data-removeonclose') == 'true') {
                dlg.parentNode.removeChild(dlg);
            }

            //resolve();
            // if we just called history.back(), then use a timeout to allow the history events to fire first
            setTimeout(function () {
                resolve({
                    element: dlg,
                    closedByBack: self.closedByBack
                });
            }, 1);
        }

        dlg.addEventListener('iron-overlay-closed', onDialogClosed);
        dlg.open();

        // It's not being positioned properly in firefox
        if (!browser.chrome && !dlg.classList.contains('fixedSize')) {
            setTimeout(function () {
                dlg.refit();
            }, 100);
        }

        if (dlg.getAttribute('data-lockscroll') == 'true' && !document.body.classList.contains('noScroll')) {
            document.body.classList.add('noScroll');
            removeScrollLockOnClose = true;
        }

        if (isHistoryEnabled(dlg)) {
            historyManager.pushState({ dialogId: hash }, "Dialog", hash);

            window.addEventListener('popstate', onHashChange);
        }
    }

    function isHistoryEnabled(dlg) {
        return dlg.getAttribute('data-history') == 'true';
    }

    function open(dlg) {

        return new Promise(function (resolve, reject) {

            new paperDialogHashHandler(dlg, 'dlg' + new Date().getTime(), resolve);
        });
    }

    function close(dlg) {

        if (dlg.opened) {
            if (isHistoryEnabled(dlg)) {
                history.back();
            } else {
                dlg.close();
            }
        }
    }

    function onDialogOpened(e) {

        focusManager.autoFocus(e.target);
    }

    function shouldLockDocumentScroll(options) {

        if (options.lockScroll != null) {
            return options.lockScroll;
        }

        if (options.size == 'fullscreen') {
            return true;
        }

        return browser.mobile;
    }

    function createDialog(options) {

        options = options || {};

        var dlg = document.createElement('paper-dialog');

        dlg.setAttribute('with-backdrop', 'with-backdrop');
        dlg.setAttribute('role', 'alertdialog');

        if (shouldLockDocumentScroll(options)) {
            dlg.setAttribute('data-lockscroll', 'true');
        }

        if (options.enableHistory !== false) {
            dlg.setAttribute('data-history', 'true');
        }

        // without this safari will scroll the background instead of the dialog contents
        // but not needed here since this is already on top of an existing dialog
        // but skip it in IE because it's causing the entire browser to hang
        // Also have to disable for firefox because it's causing select elements to not be clickable
        if (!browser.msie && !browser.firefox && options.modal !== false) {
            dlg.setAttribute('modal', 'modal');
        }

        // seeing max call stack size exceeded in the debugger with this
        dlg.setAttribute('noAutoFocus', 'noAutoFocus');

        var defaultEntryAnimation = performanceManager.getAnimationPerformance() <= 1 ? 'fade-in-animation' : 'scale-up-animation';
        dlg.entryAnimation = options.entryAnimation || defaultEntryAnimation;
        dlg.exitAnimation = 'fade-out-animation';

        // If it's not fullscreen then lower the default animation speed to make it open really fast
        var entryAnimationDuration = options.entryAnimationDuration || (options.size ? 240 : 300);

        dlg.animationConfig = {
            // scale up
            'entry': {
                name: dlg.entryAnimation,
                node: dlg,
                timing: { duration: entryAnimationDuration, easing: 'ease-out' }
            },
            // fade out
            'exit': {
                name: dlg.exitAnimation,
                node: dlg,
                timing: { duration: options.exitAnimationDuration || 400, easing: 'ease-in' }
            }
        };

        dlg.classList.add('paperDialog');

        dlg.classList.add('scrollY');

        if (layoutManager.tv || layoutManager.mobile) {
            // Need scrollbars for mouse use
            dlg.classList.add('hiddenScroll');
        }

        if (options.removeOnClose) {
            dlg.setAttribute('data-removeonclose', 'true');
        }

        if (options.size) {
            dlg.classList.add('fixedSize');
            dlg.classList.add(options.size);
        }

        if (options.autoFocus !== false) {
            dlg.addEventListener('iron-overlay-opened', onDialogOpened);
        }

        return dlg;
    }

    function positionTo(dlg, elem) {

        var windowHeight = $(window).height();

        // If the window height is under a certain amount, don't bother trying to position
        // based on an element.
        if (windowHeight >= 540) {

            var pos = $(elem).offset();

            pos.top += elem.offsetHeight / 2;
            pos.left += elem.offsetWidth / 2;

            // Account for margins
            pos.top -= 24;
            pos.left -= 24;

            // Account for popup size - we can't predict this yet so just estimate
            pos.top -= $(dlg).height() / 2;
            pos.left -= $(dlg).width() / 2;

            // Account for scroll position
            pos.top -= $(window).scrollTop();
            pos.left -= $(window).scrollLeft();

            // Avoid showing too close to the bottom
            pos.top = Math.min(pos.top, windowHeight - 300);
            pos.left = Math.min(pos.left, $(window).width() - 300);

            // Do some boundary checking
            pos.top = Math.max(pos.top, 0);
            pos.left = Math.max(pos.left, 0);

            dlg.style.position = 'fixed';
            dlg.style.left = pos.left + 'px';
            dlg.style.top = pos.top + 'px';
        }
    }

    return {
        open: open,
        close: close,
        createDialog: createDialog,
        positionTo: positionTo
    };
});