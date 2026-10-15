/**
 * AIVES Viva Audio Recorder Module (UI-03)
 * Provides HTML5 MediaRecorder and WebAudio API soundwave visualizer.
 */
class VivaAudioRecorder {
    constructor() {
        this.mediaRecorder = null;
        this.audioChunks = [];
        this.audioStream = null;
        this.audioContext = null;
        this.analyser = null;
        this.animFrameId = null;
        this.startTime = null;
        this.mimeType = this.detectSupportedMimeType();
        this.isRecording = false;
    }

    /**
     * Detects best supported audio mime type in current browser.
     */
    detectSupportedMimeType() {
        if (typeof MediaRecorder === 'undefined') return 'audio/webm';
        const types = [
            'audio/webm;codecs=opus',
            'audio/webm',
            'audio/ogg;codecs=opus',
            'audio/wav',
            'audio/mp4'
        ];
        for (const type of types) {
            if (MediaRecorder.isTypeSupported(type)) {
                return type;
            }
        }
        return 'audio/webm';
    }

    /**
     * Checks if audio recording is supported by the client browser.
     */
    isSupported() {
        return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
    }

    /**
     * Requests microphone permission.
     */
    async requestPermission() {
        if (!this.isSupported()) {
            throw new Error('Trình duyệt không hỗ trợ WebAudio hoặc MediaRecorder API.');
        }
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        stream.getTracks().forEach(t => t.stop());
        return true;
    }

    /**
     * Starts recording audio and draws waveform on canvas.
     * @param {HTMLCanvasElement} [canvasElement] Optional canvas for soundwave visualization.
     */
    async startRecording(canvasElement) {
        if (!this.isSupported()) {
            throw new Error('Trình duyệt của bạn không hỗ trợ ghi âm HTML5.');
        }

        if (this.isRecording) {
            await this.stopRecording();
        }

        try {
            this.audioStream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                }
            });

            this.audioChunks = [];
            this.startTime = Date.now();
            this.mimeType = this.detectSupportedMimeType();

            this.mediaRecorder = new MediaRecorder(this.audioStream, {
                mimeType: this.mimeType
            });

            this.mediaRecorder.ondataavailable = (event) => {
                if (event.data && event.data.size > 0) {
                    this.audioChunks.push(event.data);
                }
            };

            this.isRecording = true;
            this.mediaRecorder.start(250); // Slice every 250ms for reliable chunking

            if (canvasElement) {
                this.setupVisualizer(this.audioStream, canvasElement);
            }

            return true;
        } catch (err) {
            this.cleanup();
            if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
                throw new Error('Quyền truy cập microphone bị từ chối. Vui lòng cấp quyền trong cài đặt trình duyệt.');
            } else if (err.name === 'NotFoundError' || err.name === 'DevicesNotFoundError') {
                throw new Error('Không tìm thấy thiết bị microphone nào được kết nối.');
            } else {
                throw new Error('Lỗi khi mở microphone: ' + (err.message || err.name));
            }
        }
    }

    /**
     * Configures WebAudio analyser and visualizes soundwave on canvas.
     */
    setupVisualizer(stream, canvas) {
        try {
            const AudioCtx = window.AudioContext || window.webkitAudioContext;
            if (!AudioCtx) return;

            this.audioContext = new AudioCtx();
            const source = this.audioContext.createMediaStreamSource(stream);
            this.analyser = this.audioContext.createAnalyser();
            this.analyser.fftSize = 256;
            source.connect(this.analyser);

            const ctx = canvas.getContext('2d');
            const bufferLength = this.analyser.frequencyBinCount;
            const dataArray = new Uint8Array(bufferLength);

            const draw = () => {
                if (!this.isRecording) return;
                this.animFrameId = requestAnimationFrame(draw);

                this.analyser.getByteFrequencyData(dataArray);

                ctx.fillStyle = '#0f172a';
                ctx.fillRect(0, 0, canvas.width, canvas.height);

                const barWidth = (canvas.width / bufferLength) * 2.2;
                let x = 0;

                for (let i = 0; i < bufferLength; i++) {
                    const barHeight = (dataArray[i] / 255) * canvas.height;

                    // Gradient from cyan to blue
                    const gradient = ctx.createLinearGradient(0, canvas.height - barHeight, 0, canvas.height);
                    gradient.addColorStop(0, '#38bdf8');
                    gradient.addColorStop(1, '#2563eb');

                    ctx.fillStyle = gradient;
                    ctx.fillRect(x, canvas.height - barHeight, barWidth, barHeight);

                    x += barWidth + 1;
                }
            };

            draw();
        } catch (e) {
            console.warn('Soundwave visualizer setup failed:', e);
        }
    }

    /**
     * Stops recording and returns the recorded audio Blob.
     * @returns {Promise<{blob: Blob, durationSeconds: number, contentType: string}>}
     */
    async stopRecording() {
        if (!this.mediaRecorder || !this.isRecording) {
            return null;
        }

        return new Promise((resolve, reject) => {
            this.mediaRecorder.onstop = () => {
                const durationSeconds = Math.max(1, Math.round((Date.now() - this.startTime) / 1000));
                const audioBlob = new Blob(this.audioChunks, { type: this.mimeType });
                const contentType = this.mimeType.split(';')[0]; // e.g. "audio/webm"

                this.cleanup();
                resolve({
                    blob: audioBlob,
                    durationSeconds: durationSeconds,
                    contentType: contentType
                });
            };

            this.mediaRecorder.onerror = (e) => {
                this.cleanup();
                reject(e.error || new Error('Lỗi trong quá trình ghi âm.'));
            };

            try {
                this.mediaRecorder.stop();
            } catch (err) {
                this.cleanup();
                reject(err);
            }
        });
    }

    /**
     * Cancels recording without returning audio.
     */
    cancelRecording() {
        if (this.mediaRecorder && this.isRecording) {
            try {
                this.mediaRecorder.stop();
            } catch (e) {
                // ignore
            }
        }
        this.cleanup();
    }

    /**
     * Cleans up audio streams, analyser and tracks.
     */
    cleanup() {
        this.isRecording = false;
        if (this.animFrameId) {
            cancelAnimationFrame(this.animFrameId);
            this.animFrameId = null;
        }
        if (this.audioStream) {
            this.audioStream.getTracks().forEach(t => {
                try { t.stop(); } catch (e) { }
            });
            this.audioStream = null;
        }
        if (this.audioContext && this.audioContext.state !== 'closed') {
            try { this.audioContext.close(); } catch (e) { }
            this.audioContext = null;
        }
        this.analyser = null;
        this.mediaRecorder = null;
        this.audioChunks = [];
    }
}

window.VivaAudioRecorder = VivaAudioRecorder;
