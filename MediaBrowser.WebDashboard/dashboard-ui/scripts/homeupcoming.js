﻿define(['datetime', 'scrollStyles'], function (datetime) {

    function loadUpcoming(page) {
        Dashboard.showLoadingMsg();

        var query = {

            Limit: 40,
            Fields: "AirTime,UserData,SeriesStudio,SyncInfo",
            UserId: Dashboard.getCurrentUserId(),
            ImageTypeLimit: 1,
            EnableImageTypes: "Primary,Backdrop,Banner,Thumb",
            EnableTotalRecordCount: 0
        };

        ApiClient.getJSON(ApiClient.getUrl("Shows/Upcoming", query)).then(function (result) {

            var items = result.Items;

            if (items.length) {
                page.querySelector('.noItemsMessage').style.display = 'none';
            } else {
                page.querySelector('.noItemsMessage').style.display = 'block';
            }

            var elem = page.querySelector('#upcomingItems');
            renderUpcoming(elem, items);

            Dashboard.hideLoadingMsg();
        });
    }

    function enableScrollX() {
        return browserInfo.mobile && AppInfo.enableAppLayouts;
    }

    function getThumbShape() {
        return enableScrollX() ? 'overflowBackdrop' : 'backdrop';
    }

    function renderUpcoming(elem, items) {

        var groups = [];

        var currentGroupName = '';
        var currentGroup = [];

        var i, length;

        for (i = 0, length = items.length; i < length; i++) {

            var item = items[i];

            var dateText = '';

            if (item.PremiereDate) {
                try {

                    var premiereDate = datetime.parseISO8601Date(item.PremiereDate, true);

                    if (premiereDate.getDate() == new Date().getDate() - 1) {
                        dateText = Globalize.translate('Yesterday');
                    } else {
                        dateText = LibraryBrowser.getFutureDateText(premiereDate, true);
                    }

                } catch (err) {
                }
            }

            if (dateText != currentGroupName) {

                if (currentGroup.length) {
                    groups.push({
                        name: currentGroupName,
                        items: currentGroup
                    });
                }

                currentGroupName = dateText;
                currentGroup = [item];
            } else {
                currentGroup.push(item);
            }
        }

        var html = '';

        for (i = 0, length = groups.length; i < length; i++) {

            var group = groups[i];

            html += '<div class="homePageSection">';
            html += '<h1 class="listHeader">' + group.name + '</h1>';

            if (enableScrollX()) {
                html += '<div class="itemsContainer hiddenScrollX">';
            } else {
                html += '<div class="itemsContainer">';
            }

            html += LibraryBrowser.getPosterViewHtml({
                items: group.items,
                showLocationTypeIndicator: false,
                shape: getThumbShape(),
                showTitle: true,
                showPremiereDate: true,
                preferThumb: true,
                lazy: true,
                showDetailsMenu: true,
                centerText: true,
                context: 'home-upcoming',
                overlayMoreButton: true

            });
            html += '</div>';

            html += '</div>';
        }

        elem.innerHTML = html;
        ImageLoader.lazyChildren(elem);
    }
    return function (view, params, tabContent) {

        var self = this;

        self.renderTab = function () {

            loadUpcoming(tabContent);
        };
    };

});