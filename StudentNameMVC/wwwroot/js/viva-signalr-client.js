/**
 * AIVES Viva Realtime SignalR Client Module (UI-04, UI-05, UI-06)
 * Integrates VivaHub, MediaRecorder, TTS playback, and UX error handling.
 */
class VivaExamClient {
    constructor(config) {
        this.sessionId = config.sessionId;
        this.examId = config.examId || '';
        this.studentId = config.studentId || '';
        this.currentQuestionId = config.initialQuestionId || '';
        this.currentTurnId = null;
        this.antiForgeryToken = config.antiForgeryToken || '';
        this.submitAudioUrl = config.submitAudioUrl || '/Viva/Exam?handler=SubmitAudio';
        this.startSessionUrl = config.startSessionUrl || '/Viva/Exam?handler=StartSession';

        this.recorder = new VivaAudioRecorder();
        this.connection = null;
        this.timerInterval = null;
        this.secondsRemaining = 0;
        this.sessionState = config.initialState || 'Created';
        this.canRetry = true;

        this.initDomElements();
        this.initSignalR();
        this.bindEvents();
    }

    initDomElements() {
        this.el = {
            connectionBadge: document.getElementById('connectionBadge'),
            connectionBadgeText: document.getElementById('connectionBadgeText'),
            stateBadge: document.getElementById('sessionStateBadge'),
            turnProgressBadge: document.getElementById('turnProgressBadge'),
            timerDisplay: document.getElementById('countdownTimerDisplay'),
            timerProgress: document.getElementById('timerProgressBar'),

            // Question Section
            questionCard: document.getElementById('questionCard'),
            questionTitle: document.getElementById('questionTitle'),
            questionContent: document.getElementById('questionContent'),
            btnPlayQuestionAudio: document.getElementById('btnPlayQuestionAudio'),
            questionAudio: document.getElementById('questionAudio'),

            // Follow-up Section
            followUpCard: document.getElementById('followUpCard'),
            followUpBadge: document.getElementById('followUpBadge'),
            followUpContent: document.getElementById('followUpContent'),
            btnPlayFollowUpAudio: document.getElementById('btnPlayFollowUpAudio'),
            followUpAudio: document.getElementById('followUpAudio'),

            // Answering & Recording Section
            answeringSection: document.getElementById('answeringSection'),
            btnStartExam: document.getElementById('btnStartExam'),
            btnStartRecord: document.getElementById('btnStartRecord'),
            btnStopRecord: document.getElementById('btnStopRecord'),
            btnRetryRecord: document.getElementById('btnRetryRecord'),
            recordingStatusIndicator: document.getElementById('recordingStatusIndicator'),
            recordingStatusText: document.getElementById('recordingStatusText'),
            visualizerCanvas: document.getElementById('visualizerCanvas'),

            // Transcript Section
            transcriptCard: document.getElementById('transcriptCard'),
            transcriptText: document.getElementById('transcriptText'),
            transcriptConfidence: document.getElementById('transcriptConfidence'),
            transcriptionSpinner: document.getElementById('transcriptionSpinner'),

            // Completed Section
            completedCard: document.getElementById('completedCard'),

            // Alerts and modals
            errorToast: document.getElementById('vivaErrorToast'),
            errorToastMessage: document.getElementById('vivaErrorToastMessage'),
            errorToastCode: document.getElementById('vivaErrorToastCode'),
            generalAlert: document.getElementById('generalAlert'),
            generalAlertText: document.getElementById('generalAlertText')
        };
    }

    bindEvents() {
        if (this.el.btnStartExam) {
            this.el.btnStartExam.addEventListener('click', () => this.handleStartExam());
        }

        if (this.el.btnStartRecord) {
            this.el.btnStartRecord.addEventListener('click', () => this.handleStartRecording());
        }

        if (this.el.btnStopRecord) {
            this.el.btnStopRecord.addEventListener('click', () => this.handleStopAndSubmitRecording());
        }

        if (this.el.btnRetryRecord) {
            this.el.btnRetryRecord.addEventListener('click', () => this.handleRetryRecording());
        }

        if (this.el.btnPlayQuestionAudio) {
            this.el.btnPlayQuestionAudio.addEventListener('click', () => {
                const text = this.el.questionContent ? this.el.questionContent.textContent : '';
                this.playTts(text, this.el.questionAudio, this.el.questionAudio?.src, null);
            });
        }

        if (this.el.btnPlayFollowUpAudio) {
            this.el.btnPlayFollowUpAudio.addEventListener('click', () => {
                const text = this.el.followUpContent ? this.el.followUpContent.textContent : '';
                this.playTts(text, this.el.followUpAudio, this.el.followUpAudio?.src, null);
            });
        }
    }

    initSignalR() {
        if (typeof signalR === 'undefined') {
            this.showError('SIGNALR_NOT_LOADED', 'Thư viện SignalR chưa được tải. Vui lòng kiểm tra kết nối mạng.', true);
            return;
        }

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/viva')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // 1. SessionStatusChanged
        this.connection.on('SessionStatusChanged', (status) => {
            console.log('[SignalR] SessionStatusChanged:', status);
            this.handleSessionStatusChanged(status);
        });

        // 2. TranscriptReady
        this.connection.on('TranscriptReady', (transcript) => {
            console.log('[SignalR] TranscriptReady:', transcript);
            this.handleTranscriptReady(transcript);
        });

        // 3. FollowUpGenerated
        this.connection.on('FollowUpGenerated', (followUp) => {
            console.log('[SignalR] FollowUpGenerated:', followUp);
            this.handleFollowUpGenerated(followUp);
        });

        // 4. TimeRemainingChanged
        this.connection.on('TimeRemainingChanged', (timer) => {
            this.handleTimeRemainingChanged(timer);
        });

        // 5. SessionCompleted
        this.connection.on('SessionCompleted', (status) => {
            console.log('[SignalR] SessionCompleted:', status);
            this.handleSessionCompleted(status);
        });

        // 6. VivaError
        this.connection.on('VivaError', (error) => {
            console.warn('[SignalR] VivaError:', error);
            this.handleVivaError(error);
        });

        // Lifecycle: Reconnecting
        this.connection.onreconnecting((err) => {
            console.warn('[SignalR] Reconnecting...', err);
            this.setConnectionStatus('reconnecting', 'Đang kết nối lại...');
            this.showToast('Mất kết nối tạm thời. Đang tự động kết nối lại máy chủ...', 'warning');
        });

        // Lifecycle: Reconnected
        this.connection.onreconnected(async (connectionId) => {
            console.log('[SignalR] Reconnected. New Connection ID:', connectionId);
            this.setConnectionStatus('connected', 'Đã kết nối');
            this.showToast('Đã khôi phục kết nối thành công!', 'success');
            await this.joinAndRestoreSession();
        });

        // Lifecycle: Closed
        this.connection.onclose((err) => {
            console.error('[SignalR] Connection closed:', err);
            this.setConnectionStatus('disconnected', 'Mất kết nối');
            this.showError('CONNECTION_LOST', 'Mất kết nối với phòng thi. Vui lòng làm mới trang hoặc bấm kết nối lại.', true);
        });

        this.startConnection();
    }

    async startConnection() {
        this.setConnectionStatus('connecting', 'Đang kết nối...');
        try {
            await this.connection.start();
            this.setConnectionStatus('connected', 'Đã kết nối');
            await this.joinAndRestoreSession();
        } catch (err) {
            console.error('[SignalR] Could not start connection:', err);
            this.setConnectionStatus('disconnected', 'Lỗi kết nối');
            this.showError('INITIAL_CONNECTION_FAILED', 'Không thể kết nối đến máy chủ realtime. Hãy kiểm tra kết nối mạng.', true);
        }
    }

    async joinAndRestoreSession() {
        if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) return;

        try {
            const snapshot = await this.connection.invoke('JoinSession', this.sessionId);
            console.log('[SignalR] Joined session snapshot:', snapshot);
            if (snapshot) {
                this.renderSnapshot(snapshot);
            }
        } catch (err) {
            console.error('[SignalR] JoinSession failed:', err);
            const msg = err.message || 'Không thể tham gia phòng thi. Có thể bạn không có quyền truy cập.';
            this.showError('JOIN_FAILED', msg, false);
        }
    }

    async leaveSession() {
        console.log('[SignalR] Leaving session:', this.sessionId);
        // 1. Cancel / cleanup active recording and release microphone
        if (this.recorder) {
            try {
                this.recorder.cancelRecording();
            } catch (e) {
                console.warn('Error cancelling recorder on leave:', e);
            }
        }

        // 2. Stop audio playback if active
        if (this.audioPlayer) {
            try {
                this.audioPlayer.pause();
                this.audioPlayer.src = '';
            } catch (e) { }
        }

        // 3. Clear timers
        if (this.timerInterval) {
            clearInterval(this.timerInterval);
            this.timerInterval = null;
        }

        // 4. Notify SignalR hub
        if (this.connection && this.connection.state === signalR.HubConnectionState.Connected) {
            try {
                await this.connection.invoke('LeaveSession', this.sessionId);
                await this.connection.stop();
            } catch (e) {
                console.warn('Error invoking LeaveSession:', e);
            }
        }
    }

    renderSnapshot(snapshot) {
        if (!snapshot) return;

        if (snapshot.status) {
            this.handleSessionStatusChanged(snapshot.status);
        }

        if (snapshot.currentQuestion) {
            this.renderQuestion(snapshot.currentQuestion);
        }

        if (snapshot.latestTranscript) {
            this.handleTranscriptReady(snapshot.latestTranscript);
        }

        if (snapshot.currentFollowUp &&
            (snapshot.status?.state === 'FollowUpDelivered' || snapshot.status?.state === 'Answering' ||
             snapshot.status?.state === 7 || snapshot.status?.state === 3)) {
            this.handleFollowUpGenerated(snapshot.currentFollowUp);
        }

        if (snapshot.timer) {
            this.handleTimeRemainingChanged(snapshot.timer);
        }
    }

    setConnectionStatus(status, text) {
        if (!this.el.connectionBadge) return;
        this.el.connectionBadge.className = 'badge py-2 px-3 rounded-pill d-inline-flex align-items-center ';

        if (status === 'connected') {
            this.el.connectionBadge.classList.add('bg-success');
            this.el.connectionBadge.innerHTML = '<i class="bi bi-circle-fill text-light me-1 fs-8"></i> ' + (text || 'Đã kết nối');
        } else if (status === 'reconnecting' || status === 'connecting') {
            this.el.connectionBadge.classList.add('bg-warning', 'text-dark', 'viva-badge-pulse');
            this.el.connectionBadge.innerHTML = '<i class="bi bi-arrow-repeat spin me-1"></i> ' + (text || 'Đang kết nối...');
        } else {
            this.el.connectionBadge.classList.add('bg-danger');
            this.el.connectionBadge.innerHTML = '<i class="bi bi-slash-circle me-1"></i> ' + (text || 'Mất kết nối');
        }
    }

    handleSessionStatusChanged(status) {
        this.sessionState = status.state;
        const stateName = typeof status.state === 'string' ? status.state : this.mapStateToString(status.state);

        if (this.el.stateBadge) {
            this.el.stateBadge.textContent = this.formatStateFriendly(stateName);
            this.el.stateBadge.className = 'badge px-3 py-2 rounded-pill ' + this.getStateBadgeClass(stateName);
        }

        if (this.el.turnProgressBadge && status.currentTurnOrdinal > 0) {
            const maxTurns = status.maxTurnsAllowed || 2;
            this.el.turnProgressBadge.textContent = `Lượt thi: ${status.currentTurnOrdinal} / ${maxTurns}`;
        }

        // Handle states
        if (stateName === 'Ready') {
            if (this.el.btnStartExam) this.el.btnStartExam.classList.remove('d-none');
            if (this.el.answeringSection) this.el.answeringSection.classList.add('d-none');
        } else if (stateName === 'QuestionDelivered') {
            if (this.el.btnStartExam) this.el.btnStartExam.classList.add('d-none');
            if (this.el.questionCard) this.el.questionCard.classList.remove('d-none');
            if (this.el.answeringSection) this.el.answeringSection.classList.remove('d-none');
        } else if (stateName === 'Answering') {
            if (this.el.btnStartExam) this.el.btnStartExam.classList.add('d-none');
            if (this.el.answeringSection) this.el.answeringSection.classList.remove('d-none');
        } else if (stateName === 'Transcribing') {
            if (this.el.transcriptionSpinner) this.el.transcriptionSpinner.classList.remove('d-none');
            this.setRecordingUiState('disabled', 'AI đang chuyển giọng nói thành văn bản...');
        } else if (stateName === 'TranscriptReady') {
            if (this.el.transcriptionSpinner) this.el.transcriptionSpinner.classList.add('d-none');
        } else if (stateName === 'Completed') {
            this.handleSessionCompleted(status);
        } else if (stateName === 'TimedOut') {
            this.setRecordingUiState('disabled', 'Đã hết thời gian trả lời câu hỏi!');
            this.showToast('Hết thời gian trả lời cho lượt thi này.', 'warning');
        }
    }

    playTts(text, audioEl, audioUrl, onEndCallback) {
        // Playback via browser Web Speech API (Vietnamese voice)
        const speakBrowserTts = () => {
            if (!('speechSynthesis' in window) || !text) {
                if (onEndCallback) onEndCallback();
                return;
            }

            window.speechSynthesis.cancel();
            const utterance = new SpeechSynthesisUtterance(text);
            utterance.lang = 'vi-VN';
            utterance.rate = 1.0;

            const voices = window.speechSynthesis.getVoices();
            const viVoice = voices.find(v => v.lang && (v.lang.startsWith('vi') || v.lang.includes('VIE')));
            if (viVoice) utterance.voice = viVoice;

            let completed = false;
            const finish = () => {
                if (!completed) {
                    completed = true;
                    if (onEndCallback) onEndCallback();
                }
            };

            utterance.onend = finish;
            utterance.onerror = (e) => {
                console.warn('SpeechSynthesis warning:', e);
                finish();
            };

            window.speechSynthesis.speak(utterance);
        };

        if (audioEl && audioUrl) {
            // Play synthesized audio file directly
            audioEl.src = audioUrl;
            audioEl.currentTime = 0;
            audioEl.play()
                .then(() => {
                    if (onEndCallback) audioEl.onended = onEndCallback;
                })
                .catch(err => {
                    console.warn('Audio playback failed or blocked, falling back to speech synthesis:', err);
                    speakBrowserTts();
                });
        } else {
            speakBrowserTts();
        }
    }

    renderQuestion(question) {
        if (!question) return;
        this.currentQuestionId = question.questionId;
        if (this.el.questionTitle) this.el.questionTitle.textContent = question.title || `Câu hỏi: ${question.questionId}`;
        if (this.el.questionContent) this.el.questionContent.textContent = question.content || '';

        if (question.audioUrl && this.el.questionAudio) {
            this.el.questionAudio.src = question.audioUrl;
        }

        if (this.el.btnPlayQuestionAudio) this.el.btnPlayQuestionAudio.classList.remove('d-none');
        this.playTts(question.content, this.el.questionAudio, question.audioUrl, () => {
            this.requestBeginAnswering();
        });
    }

    async requestBeginAnswering() {
        if (!this.connection || this.connection.state !== signalR.HubConnectionState.Connected) return;
        try {
            const timer = await this.connection.invoke('BeginAnswering', this.sessionId);
            if (timer) {
                this.handleTimeRemainingChanged(timer);
            }
        } catch (e) {
            console.warn('[SignalR] BeginAnswering invocation notice:', e);
        }
    }

    handleTranscriptReady(transcript) {
        if (!transcript) return;
        this.currentTurnId = transcript.turnId;

        if (this.el.transcriptionSpinner) {
            this.el.transcriptionSpinner.classList.add('d-none');
        }

        if (this.el.transcriptCard) {
            this.el.transcriptCard.classList.remove('d-none');
        }

        if (this.el.transcriptText) {
            this.el.transcriptText.textContent = transcript.transcriptText || '(Không có nội dung nhận diện)';
            this.el.transcriptText.classList.add('fade-in');
        }

        if (this.el.transcriptConfidence && transcript.confidenceScore !== undefined) {
            const pct = Math.round(transcript.confidenceScore * 100);
            this.el.transcriptConfidence.textContent = `Độ tin cậy: ${pct}%`;
            this.el.transcriptConfidence.className = 'badge ' + (pct >= 80 ? 'bg-success' : (pct >= 50 ? 'bg-warning text-dark' : 'bg-danger'));
        }
    }

    handleFollowUpGenerated(followUp) {
        if (!followUp) return;
        this.currentQuestionId = followUp.followUpQuestionId;
        this.currentTurnId = followUp.turnId;

        if (this.el.followUpCard) {
            this.el.followUpCard.classList.remove('d-none');
            this.el.followUpCard.classList.add('viva-card-highlight');
        }

        if (this.el.followUpBadge) {
            this.el.followUpBadge.textContent = `Câu hỏi phụ #${followUp.followUpOrdinal || 1}`;
        }

        if (this.el.followUpContent) {
            this.el.followUpContent.textContent = followUp.followUpContent || '';
        }

        // Follow-up TTS Playback (UI-05)
        if (this.el.btnPlayFollowUpAudio) this.el.btnPlayFollowUpAudio.classList.remove('d-none');
        this.playTts(followUp.followUpContent, this.el.followUpAudio, followUp.audioUrl, () => {
            this.requestBeginAnswering();
            this.setRecordingUiState('ready', 'Mời bạn bắt đầu ghi âm trả lời câu hỏi phụ.');
        });

        this.showToast('AI đã tạo câu hỏi phụ cho bạn!', 'info');
    }

    handleTimeRemainingChanged(timer) {
        if (!timer) return;
        this.secondsRemaining = timer.secondsRemaining;

        if (this.el.timerDisplay) {
            const m = Math.floor(this.secondsRemaining / 60);
            const s = this.secondsRemaining % 60;
            this.el.timerDisplay.textContent = `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;

            if (this.secondsRemaining <= 15 && this.secondsRemaining > 0) {
                this.el.timerDisplay.className = 'viva-timer-box text-danger viva-badge-pulse';
            } else if (this.secondsRemaining <= 30 && this.secondsRemaining > 0) {
                this.el.timerDisplay.className = 'viva-timer-box text-warning';
            } else {
                this.el.timerDisplay.className = 'viva-timer-box text-primary';
            }
        }

        if (this.el.timerProgress && timer.totalSecondsAllowed > 0) {
            const pct = Math.max(0, Math.min(100, Math.round((this.secondsRemaining / timer.totalSecondsAllowed) * 100)));
            this.el.timerProgress.style.width = `${pct}%`;
            this.el.timerProgress.className = 'progress-bar ' + (pct <= 25 ? 'bg-danger' : (pct <= 50 ? 'bg-warning' : 'bg-primary'));
        }
    }

    handleSessionCompleted(status) {
        this.sessionState = 'Completed';
        this.recorder.cancelRecording();

        if (this.el.questionCard) this.el.questionCard.classList.add('d-none');
        if (this.el.followUpCard) this.el.followUpCard.classList.add('d-none');
        if (this.el.answeringSection) this.el.answeringSection.classList.add('d-none');

        if (this.el.completedCard) {
            this.el.completedCard.classList.remove('d-none');
        }

        if (this.el.stateBadge) {
            this.el.stateBadge.textContent = 'Hoàn thành';
            this.el.stateBadge.className = 'badge px-3 py-2 rounded-pill bg-success';
        }

        this.showToast('Chúc mừng! Bạn đã hoàn thành phiên thi vấn đáp.', 'success');
    }

    handleVivaError(error) {
        this.canRetry = error.canRetry;
        this.showError(error.errorCode, error.errorMessage, error.canRetry);

        if (error.canRetry) {
            if (this.el.btnRetryRecord) this.el.btnRetryRecord.classList.remove('d-none');
            this.setRecordingUiState('ready', 'Đã xảy ra lỗi. Bạn có thể nhấn Ghi âm lại.');
        } else {
            this.setRecordingUiState('disabled', 'Lỗi nghiêm trọng. Vui lòng liên hệ hội đồng thi.');
        }
    }

    async handleStartExam() {
        if (!this.el.btnStartExam) return;
        this.el.btnStartExam.disabled = true;
        this.el.btnStartExam.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span> Đang khởi tạo...';

        try {
            const formData = new FormData();
            formData.append('sessionId', this.sessionId);

            const response = await fetch(this.startSessionUrl, {
                method: 'POST',
                headers: {
                    'RequestVerificationToken': this.antiForgeryToken
                },
                body: formData
            });

            if (!response.ok) {
                const errJson = await response.json().catch(() => ({}));
                throw new Error(errJson.message || `Lỗi máy chủ (${response.status})`);
            }

            const data = await response.json();
            if (data.question) {
                this.renderQuestion(data.question);
            }
        } catch (err) {
            console.error('StartExam error:', err);
            this.showError('START_FAILED', err.message || 'Không thể bắt đầu phiên thi.', true);
            this.el.btnStartExam.disabled = false;
            this.el.btnStartExam.innerHTML = '<i class="bi bi-play-circle-fill me-1"></i> Bắt đầu thi';
        }
    }

    async handleStartRecording() {
        try {
            await this.recorder.startRecording(this.el.visualizerCanvas);
            this.setRecordingUiState('recording', 'Đang ghi âm câu trả lời...');
        } catch (err) {
            console.error('Recording start error:', err);
            this.showError('MIC_ERROR', err.message, true);
        }
    }

    async handleStopAndSubmitRecording() {
        this.setRecordingUiState('uploading', 'Đang nén và tải âm thanh lên máy chủ...');

        try {
            const recordResult = await this.recorder.stopRecording();
            if (!recordResult || !recordResult.blob) {
                throw new Error('Không có dữ liệu âm thanh được ghi.');
            }

            const formData = new FormData();
            formData.append('sessionId', this.sessionId);
            formData.append('questionId', this.currentQuestionId);
            formData.append('audioFile', recordResult.blob, 'recording.webm');
            formData.append('durationSeconds', recordResult.durationSeconds);

            const response = await fetch(this.submitAudioUrl, {
                method: 'POST',
                headers: {
                    'RequestVerificationToken': this.antiForgeryToken
                },
                body: formData
            });

            if (!response.ok) {
                const errData = await response.json().catch(() => ({}));
                throw new Error(errData.message || `Lỗi khi tải câu trả lời (${response.status})`);
            }

            const result = await response.json();
            console.log('Submission result:', result);
            this.setRecordingUiState('disabled', 'Đã nộp bài thành công! Đang chờ AI phân tích...');
        } catch (err) {
            console.error('Submit audio error:', err);
            this.showError('SUBMIT_AUDIO_ERROR', err.message, true);
            if (this.el.btnRetryRecord) this.el.btnRetryRecord.classList.remove('d-none');
        }
    }

    handleRetryRecording() {
        if (this.el.btnRetryRecord) this.el.btnRetryRecord.classList.add('d-none');
        this.recorder.cancelRecording();
        this.setRecordingUiState('ready', 'Sẵn sàng ghi âm lại câu trả lời.');
    }

    setRecordingUiState(state, text) {
        if (this.el.recordingStatusText && text) {
            this.el.recordingStatusText.textContent = text;
        }

        if (state === 'recording') {
            if (this.el.btnStartRecord) this.el.btnStartRecord.classList.add('d-none');
            if (this.el.btnStopRecord) this.el.btnStopRecord.classList.remove('d-none');
            if (this.el.recordingStatusIndicator) {
                this.el.recordingStatusIndicator.className = 'badge bg-danger viva-badge-pulse py-2 px-3';
                this.el.recordingStatusIndicator.innerHTML = '<i class="bi bi-record-circle-fill me-1"></i> Đang ghi âm';
            }
        } else if (state === 'uploading') {
            if (this.el.btnStartRecord) this.el.btnStartRecord.classList.add('d-none');
            if (this.el.btnStopRecord) this.el.btnStopRecord.classList.add('d-none');
            if (this.el.recordingStatusIndicator) {
                this.el.recordingStatusIndicator.className = 'badge bg-warning text-dark py-2 px-3';
                this.el.recordingStatusIndicator.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span> Đang tải lên';
            }
        } else if (state === 'ready') {
            if (this.el.btnStartRecord) {
                this.el.btnStartRecord.classList.remove('d-none');
                this.el.btnStartRecord.disabled = false;
            }
            if (this.el.btnStopRecord) this.el.btnStopRecord.classList.add('d-none');
            if (this.el.recordingStatusIndicator) {
                this.el.recordingStatusIndicator.className = 'badge bg-secondary py-2 px-3';
                this.el.recordingStatusIndicator.innerHTML = '<i class="bi bi-mic me-1"></i> Sẵn sàng';
            }
        } else {
            // disabled
            if (this.el.btnStartRecord) this.el.btnStartRecord.disabled = true;
            if (this.el.btnStopRecord) this.el.btnStopRecord.classList.add('d-none');
            if (this.el.recordingStatusIndicator) {
                this.el.recordingStatusIndicator.className = 'badge bg-light text-muted border py-2 px-3';
                this.el.recordingStatusIndicator.innerHTML = '<i class="bi bi-mic-mute me-1"></i> Đã khóa mic';
            }
        }
    }

    showError(code, message, canRetry) {
        if (this.el.errorToast) {
            if (this.el.errorToastCode) this.el.errorToastCode.textContent = code || 'LỖI';
            if (this.el.errorToastMessage) this.el.errorToastMessage.textContent = message || 'Đã xảy ra sự cố không xác định.';

            const toast = new bootstrap.Toast(this.el.errorToast, { autohide: !canRetry, delay: 8000 });
            toast.show();
        }

        if (this.el.generalAlert && this.el.generalAlertText) {
            this.el.generalAlertText.textContent = `[${code}] ${message}`;
            this.el.generalAlert.classList.remove('d-none');
        }
    }

    showToast(message, type = 'info') {
        const toastContainer = document.getElementById('toastContainer');
        if (!toastContainer) return;

        const toastEl = document.createElement('div');
        toastEl.className = `toast align-items-center text-white bg-${type} border-0 shadow`;
        toastEl.setAttribute('role', 'alert');
        toastEl.setAttribute('aria-live', 'assertive');
        toastEl.setAttribute('aria-atomic', 'true');
        toastEl.innerHTML = `
            <div class="d-flex">
                <div class="toast-body">
                    <i class="bi ${type === 'success' ? 'bi-check-circle' : (type === 'warning' ? 'bi-exclamation-triangle' : 'bi-info-circle')} me-2"></i>
                    ${message}
                </div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Đóng"></button>
            </div>
        `;
        toastContainer.appendChild(toastEl);
        const toast = new bootstrap.Toast(toastEl, { delay: 4000 });
        toast.show();
        toastEl.addEventListener('hidden.bs.toast', () => toastEl.remove());
    }

    mapStateToString(stateNum) {
        const states = [
            'Created', 'Ready', 'QuestionDelivered', 'Answering',
            'Transcribing', 'TranscriptReady', 'FollowUpGenerating',
            'FollowUpDelivered', 'Completed', 'Failed', 'TimedOut'
        ];
        return states[stateNum] || 'Unknown';
    }

    formatStateFriendly(state) {
        const map = {
            'Created': 'Khởi tạo',
            'Ready': 'Sẵn sàng thi',
            'QuestionDelivered': 'Đã phát câu hỏi',
            'Answering': 'Đang trả lời',
            'Transcribing': 'Đang nhận diện giọng nói',
            'TranscriptReady': 'Đã nhận diện văn bản',
            'FollowUpGenerating': 'AI đang tạo câu hỏi phụ',
            'FollowUpDelivered': 'Câu hỏi phụ AI',
            'Completed': 'Đã hoàn thành',
            'Failed': 'Lỗi phiên thi',
            'TimedOut': 'Hết giờ'
        };
        return map[state] || state;
    }

    getStateBadgeClass(state) {
        const map = {
            'Created': 'bg-secondary',
            'Ready': 'bg-info text-dark',
            'QuestionDelivered': 'bg-primary',
            'Answering': 'bg-danger viva-badge-pulse',
            'Transcribing': 'bg-info text-dark',
            'TranscriptReady': 'bg-success',
            'FollowUpGenerating': 'bg-warning text-dark',
            'FollowUpDelivered': 'bg-primary',
            'Completed': 'bg-success',
            'Failed': 'bg-danger',
            'TimedOut': 'bg-dark'
        };
        return map[state] || 'bg-secondary';
    }
}

window.VivaExamClient = VivaExamClient;
