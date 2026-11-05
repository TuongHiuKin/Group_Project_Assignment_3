// G4-RT-05 — Lecturer grading realtime client.
// Listens to GradingStarted / GradingCompleted / GradingFailed on the grading-{examId} group
// and updates status badges and scores without a page reload (F5).
//
// Requires the SignalR browser client (same as _VivaLayout):
//   <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js"></script>
//   <script src="~/js/grading-realtime.js" asp-append-version="true"></script>
//
// Markup contract (see docs/G4-RT-SIGNALR-HANDOFF.md):
//   <div data-grading-realtime data-exam-id="EXAM01" [data-status-url="/Grading?handler=Status&examId=EXAM01"]>
//     <span data-grading-connection></span>            optional connection indicator
//     <div data-grading-message></div>                 optional feedback for regrade requests
//     <tr data-grading-session="{sessionId}">          session row (Index)
//       <span data-grading-badge></span>
//       <span data-grading-score></span>
//     <div data-grading-session="{sessionId}" data-grading-question="{questionId}">   question card (Review)
//       <span data-grading-badge></span> <span data-grading-score></span>
//       <button data-grading-regrade data-session-id="..." data-question-id="...">Chấm lại bằng AI</button>
(function (window, document) {
    'use strict';

    var RECONNECT_DELAYS = [0, 2000, 5000, 10000];

    // Labels follow docs/G4-CONTRACTS.md §7.
    var BADGES = {
        pending: { text: 'Chờ chấm điểm', cls: 'badge bg-secondary' },
        grading: { text: 'Đang chấm điểm…', cls: 'badge bg-info text-dark', spinner: true },
        aiGraded: { text: 'AI chấm, chờ giảng viên duyệt', cls: 'badge bg-warning text-dark' },
        finalized: { text: 'Điểm chính thức', cls: 'badge bg-success' },
        failed: { text: 'Cần chấm lại', cls: 'badge bg-danger' }
    };

    // GradingState: 0 Pending, 1 AiGraded, 2 Finalized, 3 Failed.
    function badgeKeyForState(state) {
        switch (Number(state)) {
            case 1: return 'aiGraded';
            case 2: return 'finalized';
            case 3: return 'failed';
            default: return 'pending';
        }
    }

    var REGRADE_MESSAGES = {
        Queued: 'Đã đưa vào hàng đợi chấm lại.',
        AlreadyInProgress: 'Bài này đang được chấm, vui lòng chờ.',
        AlreadyFinalized: 'Điểm đã được giảng viên chốt, không thể chấm lại bằng AI.',
        SessionNotFound: 'Không tìm thấy bài thi.',
        SessionNotCompleted: 'Bài thi chưa hoàn thành.',
        QueueUnavailable: 'Hệ thống chấm điểm đang bận, vui lòng thử lại sau.'
    };

    function cssEscape(value) {
        return window.CSS && window.CSS.escape ? window.CSS.escape(String(value)) : String(value).replace(/["\\]/g, '\\$&');
    }

    function formatScore(value) {
        var n = Number(value);
        return isFinite(n) ? n.toLocaleString('vi-VN', { maximumFractionDigits: 2 }) : '—';
    }

    function setBadge(el, key) {
        var spec = BADGES[key];
        if (!el || !spec) return;
        el.className = spec.cls;
        el.textContent = '';
        if (spec.spinner) {
            var spinner = document.createElement('span');
            spinner.className = 'spinner-border spinner-border-sm me-1';
            spinner.setAttribute('role', 'status');
            spinner.setAttribute('aria-hidden', 'true');
            el.appendChild(spinner);
        }
        el.appendChild(document.createTextNode(spec.text));
        el.setAttribute('data-grading-state', key);
    }

    function setText(el, text) {
        if (el) el.textContent = text;
    }

    function within(target, selector) {
        if (target.matches(selector)) return [target];
        return Array.prototype.slice.call(target.querySelectorAll(selector));
    }

    function GradingRealtime(root, options) {
        this.root = root;
        this.examId = (options && options.examId) || root.getAttribute('data-exam-id');
        this.statusUrl = (options && options.statusUrl) || root.getAttribute('data-status-url');
        this.hubUrl = (options && options.hubUrl) || root.getAttribute('data-hub-url') || '/hubs/viva';
        // sessionId -> { questionId -> badge key }; used to derive the session-level badge.
        this.sessions = {};
        this.connection = null;
    }

    GradingRealtime.prototype.questionTargets = function (sessionId, questionId) {
        var s = cssEscape(sessionId), q = cssEscape(questionId);
        var selector = '[data-grading-session="' + s + '"][data-grading-question="' + q + '"],' +
            '[data-grading-session="' + s + '"] [data-grading-question="' + q + '"]';
        return Array.prototype.slice.call(this.root.querySelectorAll(selector));
    };

    GradingRealtime.prototype.sessionTargets = function (sessionId) {
        var selector = '[data-grading-session="' + cssEscape(sessionId) + '"]:not([data-grading-question])';
        return Array.prototype.slice.call(this.root.querySelectorAll(selector));
    };

    GradingRealtime.prototype.update = function (sessionId, questionId, key, score) {
        if (!sessionId || !questionId) return;
        var tracked = this.sessions[sessionId] || (this.sessions[sessionId] = {});
        tracked[questionId] = key;

        this.questionTargets(sessionId, questionId).forEach(function (target) {
            within(target, '[data-grading-badge]').forEach(function (el) { setBadge(el, key); });
            if (score) within(target, '[data-grading-score]').forEach(function (el) { setText(el, score); });
            within(target, '[data-grading-regrade]').forEach(function (btn) {
                btn.disabled = key === 'grading' || key === 'finalized';
            });
        });

        // Session row: grading if any question is grading, failed if any failed,
        // official only when every tracked question is finalized, otherwise AI-graded.
        var states = Object.keys(tracked).map(function (k) { return tracked[k]; });
        var sessionKey = states.indexOf('grading') >= 0 ? 'grading'
            : states.indexOf('failed') >= 0 ? 'failed'
            : states.every(function (s) { return s === 'finalized'; }) ? 'finalized'
            : states.indexOf('pending') >= 0 ? 'pending' : 'aiGraded';
        this.sessionTargets(sessionId).forEach(function (row) {
            within(row, '[data-grading-badge]').forEach(function (el) { setBadge(el, sessionKey); });
        });

        this.root.dispatchEvent(new CustomEvent('grading:update', {
            bubbles: true,
            detail: { sessionId: sessionId, questionId: questionId, state: key, score: score || null }
        }));
    };

    GradingRealtime.prototype.onStarted = function (dto) {
        this.update(dto.sessionId, dto.questionId, 'grading');
    };

    GradingRealtime.prototype.onCompleted = function (dto) {
        var score = formatScore(dto.earnedScore) + '/' + formatScore(dto.maxScore);
        this.update(dto.sessionId, dto.questionId, badgeKeyForState(dto.state), score);
    };

    GradingRealtime.prototype.onFailed = function (dto) {
        this.update(dto.sessionId, dto.questionId, 'failed');
        this.message(dto.errorMessage || BADGES.failed.text, 'danger');
    };

    GradingRealtime.prototype.message = function (text, tone) {
        var box = this.root.querySelector('[data-grading-message]');
        if (!box) return;
        box.className = 'alert alert-' + (tone || 'info') + ' py-2';
        box.textContent = text;
    };

    GradingRealtime.prototype.connectionStatus = function (text) {
        setText(this.root.querySelector('[data-grading-connection]'), text);
    };

    // Events missed while disconnected are re-read from the server (persisted state is the source of truth).
    // Expected JSON: [{ sessionId, questionId, state, earnedScore, maxScore }] or { items: [...] }.
    GradingRealtime.prototype.resync = function () {
        var self = this;
        if (!self.statusUrl || !window.fetch) return Promise.resolve();
        return window.fetch(self.statusUrl, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (data) {
                var items = Array.isArray(data) ? data : (data && data.items) || [];
                items.forEach(function (item) {
                    var key = badgeKeyForState(item.state);
                    var score = item.earnedScore != null && item.maxScore != null
                        ? formatScore(item.earnedScore) + '/' + formatScore(item.maxScore) : null;
                    self.update(item.sessionId, item.questionId, key, score);
                });
            })
            .catch(function (err) { console.warn('[grading] resync failed', err); });
    };

    GradingRealtime.prototype.join = function () {
        var self = this;
        return self.connection.invoke('JoinGradingGroup', self.examId).then(function () {
            self.connectionStatus('Đang cập nhật trực tiếp');
        });
    };

    GradingRealtime.prototype.requestRegrade = function (sessionId, questionId) {
        var self = this;
        if (!self.connection || self.connection.state !== window.signalR.HubConnectionState.Connected) {
            self.message('Mất kết nối realtime, vui lòng thử lại sau.', 'warning');
            return Promise.resolve(null);
        }
        return self.connection.invoke('RequestRegrade', sessionId, questionId || null)
            .then(function (result) {
                self.message(REGRADE_MESSAGES[result] || result, result === 'Queued' ? 'success' : 'warning');
                if (result === 'Queued' && questionId) self.update(sessionId, questionId, 'pending');
                return result;
            })
            .catch(function (err) {
                console.warn('[grading] regrade failed', err);
                self.message('Không thể gửi yêu cầu chấm lại.', 'danger');
                return null;
            });
    };

    GradingRealtime.prototype.start = function () {
        var self = this;
        if (!window.signalR) {
            console.error('[grading] SignalR client library is missing.');
            return Promise.resolve(null);
        }
        if (!self.examId) {
            console.error('[grading] data-exam-id is required.');
            return Promise.resolve(null);
        }

        self.connection = new window.signalR.HubConnectionBuilder()
            .withUrl(self.hubUrl)
            .withAutomaticReconnect(RECONNECT_DELAYS)
            .build();

        self.connection.on('GradingStarted', function (dto) { self.onStarted(dto); });
        self.connection.on('GradingCompleted', function (dto) { self.onCompleted(dto); });
        self.connection.on('GradingFailed', function (dto) { self.onFailed(dto); });

        self.connection.onreconnecting(function () { self.connectionStatus('Đang kết nối lại…'); });
        self.connection.onreconnected(function () {
            // Group membership is per connection: rejoin, then catch up on missed events.
            self.join().then(function () { return self.resync(); })
                .catch(function (err) { console.warn('[grading] rejoin failed', err); });
        });
        self.connection.onclose(function () {
            self.connectionStatus('Mất kết nối realtime — thử lại sau 10 giây');
            window.setTimeout(function () { self.connect(); }, 10000);
        });

        self.root.addEventListener('click', function (e) {
            var btn = e.target.closest ? e.target.closest('[data-grading-regrade]') : null;
            if (!btn || !self.root.contains(btn)) return;
            e.preventDefault();
            if (btn.disabled) return;
            btn.disabled = true;
            self.requestRegrade(btn.getAttribute('data-session-id'), btn.getAttribute('data-question-id'))
                .then(function (result) { if (result !== 'Queued') btn.disabled = false; });
        });

        return self.connect();
    };

    GradingRealtime.prototype.connect = function () {
        var self = this;
        self.connectionStatus('Đang kết nối…');
        return self.connection.start()
            .then(function () { return self.join(); })
            .then(function () { return self.resync(); })
            .then(function () { return self; })
            .catch(function (err) {
                console.warn('[grading] connection failed', err);
                self.connectionStatus('Không kết nối được realtime — thử lại sau 10 giây');
                window.setTimeout(function () {
                    if (self.connection.state === window.signalR.HubConnectionState.Disconnected) self.connect();
                }, 10000);
                return null;
            });
    };

    var api = {
        start: function (root, options) {
            var el = typeof root === 'string' ? document.querySelector(root) : root;
            if (!el) return Promise.resolve(null);
            if (el.__gradingRealtime) return Promise.resolve(el.__gradingRealtime);
            var instance = new GradingRealtime(el, options || {});
            el.__gradingRealtime = instance;
            return instance.start();
        },
        badgeKeyForState: badgeKeyForState,
        badges: BADGES
    };
    window.AivesGradingRealtime = api;

    function autoStart() {
        Array.prototype.forEach.call(document.querySelectorAll('[data-grading-realtime]'), function (el) {
            api.start(el);
        });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', autoStart);
    else autoStart();
})(window, document);
