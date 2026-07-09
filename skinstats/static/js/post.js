function display(data, loadTime) {
    try {
        iteminfo = JSON.parse(data);

        document.getElementById('item_name').innerHTML = iteminfo.weapon + ' | ' + iteminfo.skin + " <span class=\"pop\">" + iteminfo.special + "</span>";
        document.getElementById('item_name').classList.remove('knife');
        if (isKnife(iteminfo.weapon)) {
            document.getElementById('item_name').classList.add('knife');
        }
        document.getElementById('item_paintwear').innerHTML = iteminfo.paintwear;
        document.getElementById('item_itemid').innerHTML = iteminfo.itemid;
        document.getElementById('item_paintseed').innerHTML = iteminfo.paintseed;
        document.getElementById('status').innerHTML = "Loaded in " + loadTime + " seconds";
        document.getElementById('stattrak-indicator').classList.remove('yes');
        if (iteminfo.stattrak) {
            document.getElementById('stattrak-indicator').classList.add('yes');
        }
    } catch (e) {
        document.getElementById('item_name').innerHTML = "-";
        document.getElementById('item_name').classList.remove('knife');
        document.getElementById('item_paintwear').innerHTML = '-';
        document.getElementById('item_itemid').innerHTML = '-';
        document.getElementById('item_paintseed').innerHTML = '-';
        document.getElementById('stattrak-indicator').classList.remove('yes');
        document.getElementById('status').innerHTML = data;
    }
}

window.onload = function () {
    document.getElementById("textbox").addEventListener("keydown", function (event) {
        if (event.keyCode === 13) {
            event.preventDefault();
            document.getElementById("button").click();
        }
    });

    document.getElementById("button").onclick = function (element) {
        element.toElement.blur();

        var box = document.getElementById("textbox").value;

        try {
            var match = box.match(/([SM])(\d+)A(\d+)D(\d+)$/);
            box = match[0];
            type = match[1];
            var requestData = {
                s: type === 'S' ? match[2] : '0',
                a: match[3],
                d: match[4],
                m: type === 'S' ? '0' : match[2]
            };

            document.getElementById('textbox').value = box;
            window.location.hash = box;

            post(requestData);
        } catch (e) {
            document.getElementById('textbox').value = "Not a valid inspect link";
        }
    };

    if (window.location.hash) {
        var hashURL = window.location.hash.substring(1);
        document.getElementById('textbox').value = hashURL;
        document.getElementById('button').click();
    } else {
        post({s: '76561198261551396', a: '12256887280', d: '2776544801323831695', m: '0'});
    }

    ping();
    setInterval(ping, 30000);
};

function jsonToUrl(json) {
    s = "?";
    for (var key in json) {
        s += key + "=" + json[key] + "&";
    }
    return s.substring(0, s.length - 1);
}

function post(requestData) {
    var start = performance.now();

    var request = new XMLHttpRequest();
    request.open('GET', '/api'+jsonToUrl(requestData), true);
    request.onload = function () {
        display(request.response, ((performance.now() - start) / 1000).toFixed(2));
    };

    request.send();
}

function ping() {
    var start = performance.now();

    var request = new XMLHttpRequest();
    request.open('POST', '/ping', true);
    request.setRequestHeader("Content-type", "application/x-www-form-urlencoded");
    request.onreadystatechange = function () {
        if (request.readyState === XMLHttpRequest.DONE && request.status === 200 && request.response === 'pong') {
            document.getElementById('ping').innerHTML = 'Ping:' + Math.floor((performance.now() - start)).toString() + 'ms';
        }
    };

    request.send('ping');
}

var knives = ['Bayonet', 'Butterfly Knife', 'Falchion Knife', 'Flip Knife', 'Gut Knife',
    'Huntsman Knife', 'Karambit', 'M9 Bayonet', 'Shadow Daggers', 'Bowie Knife'];

function isKnife(item_name) {
    return knives.indexOf(item_name) > -1;
}
