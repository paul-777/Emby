﻿define(['jQuery'], function ($) {

    function loadPage(page, config) {

        $('.liveTvSettingsForm', page).show();
        $('.noLiveTvServices', page).hide();

        $('#selectGuideDays', page).val(config.GuideDays || '');

        $('#chkMovies', page).checked(config.EnableMovieProviders);
        $('#chkOrganize', page).checked(config.EnableAutoOrganize);
        $('#chkConvertRecordings', page).checked(config.EnableRecordingEncoding);
        $('#chkPreserveAudio', page).checked(config.EnableOriginalAudioWithEncodedRecordings || false);

        $('#txtPrePaddingMinutes', page).val(config.PrePaddingSeconds / 60);
        $('#txtPostPaddingMinutes', page).val(config.PostPaddingSeconds / 60);

        page.querySelector('#txtRecordingPath').value = config.RecordingPath || '';
        page.querySelector('#txtMovieRecordingPath').value = config.MovieRecordingPath || '';
        page.querySelector('#txtSeriesRecordingPath').value = config.SeriesRecordingPath || '';

        page.querySelector('#chkEnableRecordingSubfolders').checked = config.EnableRecordingSubfolders || false;

        Dashboard.hideLoadingMsg();
    }

    function onSubmit() {

        Dashboard.showLoadingMsg();

        var form = this;

        ApiClient.getNamedConfiguration("livetv").then(function (config) {

            config.GuideDays = $('#selectGuideDays', form).val() || null;
            config.EnableMovieProviders = $('#chkMovies', form).checked();
            config.EnableAutoOrganize = $('#chkOrganize', form).checked();
            config.EnableRecordingEncoding = $('#chkConvertRecordings', form).checked();
            config.EnableOriginalAudioWithEncodedRecordings = $('#chkPreserveAudio', form).checked();
            config.RecordingPath = form.querySelector('#txtRecordingPath').value || null;
            config.MovieRecordingPath = form.querySelector('#txtMovieRecordingPath').value || null;
            config.SeriesRecordingPath = form.querySelector('#txtSeriesRecordingPath').value || null;

            config.PrePaddingSeconds = $('#txtPrePaddingMinutes', form).val() * 60;
            config.PostPaddingSeconds = $('#txtPostPaddingMinutes', form).val() * 60;
            config.EnableRecordingSubfolders = form.querySelector('#chkEnableRecordingSubfolders').checked;

            ApiClient.updateNamedConfiguration("livetv", config).then(Dashboard.processServerConfigurationUpdateResult);
        });

        // Disable default form submission
        return false;
    }

    function getTabs() {
        return [
        {
            href: 'livetvstatus.html',
            name: Globalize.translate('TabDevices')
        },
         {
             href: 'livetvsettings.html',
             name: Globalize.translate('TabSettings')
         },
         {
             href: 'appservices.html?context=livetv',
             name: Globalize.translate('TabServices')
         }];
    }

    $(document).on('pageinit', "#liveTvSettingsPage", function () {

        var page = this;

        $('.liveTvSettingsForm').off('submit', onSubmit).on('submit', onSubmit);

        $('#btnSelectRecordingPath', page).on("click.selectDirectory", function () {

            require(['directorybrowser'], function (directoryBrowser) {

                var picker = new directoryBrowser();

                picker.show({

                    callback: function (path) {

                        if (path) {
                            $('#txtRecordingPath', page).val(path);
                        }
                        picker.close();
                    }
                });
            });
        });

        $('#btnSelectMovieRecordingPath', page).on("click.selectDirectory", function () {

            require(['directorybrowser'], function (directoryBrowser) {

                var picker = new directoryBrowser();

                picker.show({

                    callback: function (path) {

                        if (path) {
                            $('#txtMovieRecordingPath', page).val(path);
                        }
                        picker.close();
                    }
                });
            });
        });

        $('#btnSelectSeriesRecordingPath', page).on("click.selectDirectory", function () {

            require(['directorybrowser'], function (directoryBrowser) {

                var picker = new directoryBrowser();

                picker.show({

                    callback: function (path) {

                        if (path) {
                            $('#txtSeriesRecordingPath', page).val(path);
                        }
                        picker.close();
                    }
                });
            });
        });

    }).on('pageshow', "#liveTvSettingsPage", function () {

        LibraryMenu.setTabs('livetvadmin', 1, getTabs);
        Dashboard.showLoadingMsg();

        var page = this;

        ApiClient.getNamedConfiguration("livetv").then(function (config) {

            loadPage(page, config);

        });

        if (AppInfo.enableSupporterMembership) {
            page.querySelector('.btnSupporterForConverting a').href = 'https://emby.media/premiere';
        } else {
            page.querySelector('.btnSupporterForConverting a').href = '#';
        }

    });

});
