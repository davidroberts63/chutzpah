﻿/// <reference path="../code/code.js" />


function spinWait(millis) {
    var date = new Date();
    var curDate;
    do { curDate = new Date(); }
    while (curDate - date < millis);
}

test("Spin wait test", function () {
    spinWait(1000);
    ok(true, "this test is just long enough");
});
