﻿(function ($, window, document) {

    function loadForm(page, user) {

        page.querySelector('#txtSyncPath').value = AppSettings.syncPath();
        page.querySelector('#chkWifi').checked = AppSettings.syncOnlyOnWifi();
        page.querySelector('#chkSyncLosslessAudio').checked = AppSettings.syncLosslessAudio();

        var uploadServers = AppSettings.cameraUploadServers();

        page.querySelector('.uploadServerList').innerHTML = ConnectionManager.getSavedServers().map(function (s) {

            var checkedHtml = uploadServers.indexOf(s.Id) == -1 ? '' : ' checked';
            var html = '<paper-checkbox' + checkedHtml + ' class="chkUploadServer" data-id="' + s.Id + '">' + s.Name + '</paper-checkbox>';

            return html;

        }).join('');

        Dashboard.hideLoadingMsg();
    }

    function saveUser(page, user) {

        AppSettings.syncPath(page.querySelector('#txtSyncPath').value);
        AppSettings.syncOnlyOnWifi(page.querySelector('#chkWifi').checked);
        AppSettings.syncLosslessAudio(page.querySelector('#chkSyncLosslessAudio').checked);

        AppSettings.cameraUploadServers($(".chkUploadServer", page).get().filter(function (i) {

            return i.checked;

        }).map(function (i) {

            return i.getAttribute('data-id');
        }));

        Dashboard.hideLoadingMsg();
        Dashboard.alert(Globalize.translate('SettingsSaved'));
    }

    function onSubmit() {

        var page = $(this).parents('.page')[0];

        Dashboard.showLoadingMsg();

        var userId = getParameterByName('userId') || Dashboard.getCurrentUserId();

        ApiClient.getUser(userId).then(function (user) {

            saveUser(page, user);

        });

        // Disable default form submission
        return false;
    }

    $(document).on('pageinit', "#syncPreferencesPage", function () {

        var page = this;

        $('form', page).off('submit', onSubmit).on('submit', onSubmit);

        $('.btnSelectSyncPath', page).on('click', function () {

            require(['nativedirectorychooser'], function () {
                NativeDirectoryChooser.chooseDirectory().then(function (path) {
                    $('#txtSyncPath', page).val(path);
                });
            });
        });

    }).on('pageshow', "#syncPreferencesPage", function () {

        var page = this;

        Dashboard.showLoadingMsg();

        var userId = getParameterByName('userId') || Dashboard.getCurrentUserId();

        ApiClient.getUser(userId).then(function (user) {

            loadForm(page, user);
        });

        if (AppInfo.supportsSyncPathSetting) {
            page.querySelector('.fldSyncPath').classList.remove('hide');
        } else {
            page.querySelector('.fldSyncPath').classList.add('hide');
        }
    });

})(jQuery, window, document);