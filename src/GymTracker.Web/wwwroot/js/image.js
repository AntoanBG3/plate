// Shrinks a picked image on the phone before it is sent (SPEC 6.6, 11): drawn no larger than maxSide as JPEG, with
// quality and then size stepping down until it fits maxBytes. An <img> decodes whatever the browser can show, HEIC
// included on Safari; anything it can't decode, or can't fit, throws.
window.gymImage = {
    async shrink(file, { maxSide, quality, maxBytes, steps }) {
        const url = URL.createObjectURL(file);
        try {
            const img = new Image();
            img.src = url;
            await img.decode();
            for (const [side, q] of [[maxSide, quality], ...steps]) {
                const scale = Math.min(1, side / Math.max(img.naturalWidth, img.naturalHeight));
                const canvas = document.createElement("canvas");
                canvas.width = Math.round(img.naturalWidth * scale);
                canvas.height = Math.round(img.naturalHeight * scale);
                canvas.getContext("2d").drawImage(img, 0, 0, canvas.width, canvas.height);
                const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", q));
                if (!blob) {
                    throw new Error("encode");
                }
                if (blob.size <= maxBytes) {
                    return { blob, width: canvas.width, height: canvas.height };
                }
            }
            throw new Error("too large");
        } finally {
            URL.revokeObjectURL(url);
        }
    },
};
