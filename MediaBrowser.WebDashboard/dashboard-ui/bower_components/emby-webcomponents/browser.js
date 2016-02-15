﻿define(['isMobile'], function (isMobile) {

    var uaMatch = function (ua) {
        ua = ua.toLowerCase();

        var match = /(edge)[ \/]([\w.]+)/.exec(ua) ||
            /(opera)(?:.*version|)[ \/]([\w.]+)/.exec(ua) ||
            /(opr)(?:.*version|)[ \/]([\w.]+)/.exec(ua) ||
            /(chrome)[ \/]([\w.]+)/.exec(ua) ||
            /(safari)[ \/]([\w.]+)/.exec(ua) ||
            /(firefox)[ \/]([\w.]+)/.exec(ua) ||
            /(msie) ([\w.]+)/.exec(ua) ||
            ua.indexOf("compatible") < 0 && /(mozilla)(?:.*? rv:([\w.]+)|)/.exec(ua) ||
            [];

        var platform_match = /(ipad)/.exec(ua) ||
            /(iphone)/.exec(ua) ||
            /(android)/.exec(ua) ||
            [];

        var browser = match[1] || "";

        if (browser == "edge") {
            platform_match = [""];
        } else {
            if (ua.indexOf("windows phone") != -1 || ua.indexOf("iemobile") != -1) {

                // http://www.neowin.net/news/ie11-fakes-user-agent-to-fool-gmail-in-windows-phone-81-gdr1-update
                browser = "msie";
            }
            else if (ua.indexOf("like gecko") != -1 && ua.indexOf('webkit') == -1 && ua.indexOf('opera') == -1 && ua.indexOf('chrome') == -1 && ua.indexOf('safari') == -1) {
                browser = "msie";
            }
        }

        if (browser == 'opr') {
            browser = 'opera';
        }

        return {
            browser: browser,
            version: match[2] || "0",
            platform: platform_match[0] || ""
        };
    };

    var userAgent = window.navigator.userAgent;
    var matched = uaMatch(userAgent);
    var browser = {};

    if (matched.browser) {
        browser[matched.browser] = true;
        browser.version = matched.version;
    }

    if (matched.platform) {
        browser[matched.platform] = true;
    }

    if (!browser.chrome && !browser.msie && !browser.edge && !browser.opera && userAgent.toLowerCase().indexOf("webkit") != -1) {
        browser.safari = true;
    }

    if (isMobile.any) {
        browser.mobile = true;
    }

    browser.animate = document.documentElement.animate != null;

    return browser;
});