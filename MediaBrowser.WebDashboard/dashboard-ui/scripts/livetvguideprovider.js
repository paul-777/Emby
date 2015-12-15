﻿(function ($, document, window) {

    function init(page, type, providerId) {

        var url = 'components/tvproviders/' + type + '.js';

        require([url], function (factory) {

            var instance = new factory(page, providerId, {
            });

            instance.init();
        });
    }

    function loadTemplate(page, type, providerId) {

        var xhr = new XMLHttpRequest();
        xhr.open('GET', 'components/tvproviders/' + type + '.template.html', true);

        xhr.onload = function (e) {

            var html = this.response;
            var elem = page.querySelector('.providerTemplate');
            elem.innerHTML = Globalize.translateDocument(html);
            $(elem).trigger('create');

            init(page, type, providerId);
        }

        xhr.send();
    }

    $(document).on('pageshow', "#liveTvGuideProviderPage", function () {

        Dashboard.showLoadingMsg();

        var providerId = getParameterByName('id');
        var type = getParameterByName('type');
        var page = this;
        loadTemplate(page, type, providerId);
    });

})(jQuery, document, window);
