const uploadForm = document.querySelector("#uploadForm");
const fileInput = document.querySelector("#fileInput");
const uploadStatus = document.querySelector("#uploadStatus");
const documentsNode = document.querySelector("#documents");
const askForm = document.querySelector("#askForm");
const questionInput = document.querySelector("#questionInput");
const chatStatus = document.querySelector("#chatStatus");
const answerNode = document.querySelector("#answer");
const citationsNode = document.querySelector("#citations");

async function fetchDocuments() {
    const response = await fetch("/api/documents");
    if (!response.ok) {
        throw new Error("Document status could not be loaded.");
    }

    return response.json();
}

async function readResponsePayload(response) {
    const contentType = response.headers.get("content-type") ?? "";
    if (contentType.includes("application/json")) {
        return response.json();
    }

    const text = await response.text();
    return { error: text || response.statusText || `HTTP ${response.status}` };
}

function renderDocuments(documents) {
    if (documents.length === 0) {
        documentsNode.innerHTML = `<div class="row-meta">No documents uploaded.</div>`;
        return;
    }

    documentsNode.innerHTML = documents.map(document => {
        const failedClass = document.status === "Failed" ? " failed" : "";
        const detail = renderDocumentDetail(document);

        return `
            <div class="document-row">
                <span class="row-title">${escapeHtml(document.fileName)}</span>
                <span class="badge${failedClass}">${escapeHtml(document.status)}</span>
                ${detail}
            </div>
        `;
    }).join("");
}

function renderDocumentDetail(document) {
    if (document.errorMessage) {
        return `<span class="row-meta">${escapeHtml(document.errorMessage)}</span>`;
    }

    if (document.status === "Pending" || document.status === "Processing") {
        const percent = clampPercent(document.progressPercent ?? 0);
        const chunkDetail = document.totalChunks > 0
            ? ` · ${document.processedChunks}/${document.totalChunks} chunks`
            : "";

        return `
            <div class="progress-detail">
                <div class="progress-meta">
                    <span>${escapeHtml(document.progressStage ?? document.status)}</span>
                    <span>${percent}%${chunkDetail}</span>
                </div>
                <div class="progress-track" aria-label="Ingestion progress">
                    <span style="width: ${percent}%"></span>
                </div>
            </div>
        `;
    }

    return `<span class="row-meta">${document.chunkCount} chunks</span>`;
}

function clampPercent(value) {
    const percent = Number(value);
    if (!Number.isFinite(percent)) {
        return 0;
    }

    return Math.max(0, Math.min(100, Math.round(percent)));
}

async function refreshDocuments() {
    try {
        renderDocuments(await fetchDocuments());
    } catch (error) {
        documentsNode.innerHTML = `<div class="row-meta">${escapeHtml(error.message)}</div>`;
    }
}

uploadForm.addEventListener("submit", async event => {
    event.preventDefault();
    const file = fileInput.files?.[0];
    if (!file) {
        uploadStatus.textContent = "Choose a file";
        return;
    }

    const body = new FormData();
    body.append("file", file);

    uploadStatus.textContent = "Uploading";
    uploadForm.querySelector("button").disabled = true;

    try {
        const response = await fetch("/api/documents", { method: "POST", body });
        const payload = await readResponsePayload(response);
        if (!response.ok) {
            throw new Error(payload.error ?? "Upload failed.");
        }

        fileInput.value = "";
        uploadStatus.textContent = "Queued";
        await refreshDocuments();
    } catch (error) {
        uploadStatus.textContent = error.message;
    } finally {
        uploadForm.querySelector("button").disabled = false;
    }
});

askForm.addEventListener("submit", async event => {
    event.preventDefault();
    const question = questionInput.value.trim();
    if (!question) {
        chatStatus.textContent = "Enter a question";
        return;
    }

    chatStatus.textContent = "Thinking";
    askForm.querySelector("button").disabled = true;
    answerNode.classList.remove("empty");
    answerNode.textContent = "";
    citationsNode.innerHTML = "";

    try {
        const response = await fetch("/api/ask", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ question })
        });
        const payload = await readResponsePayload(response);
        if (!response.ok) {
            throw new Error(payload.error ?? "Question failed.");
        }

        answerNode.textContent = payload.answer;
        renderCitations(payload.citations ?? []);
        chatStatus.textContent = "Answered";
    } catch (error) {
        answerNode.textContent = error.message;
        chatStatus.textContent = "Failed";
    } finally {
        askForm.querySelector("button").disabled = false;
    }
});

function renderCitations(citations) {
    citationsNode.innerHTML = citations.map((citation, index) => `
        <div class="citation-row">
            <span class="row-title">[${index + 1}] ${escapeHtml(citation.fileName)}</span>
            <span class="row-meta">${escapeHtml(citationLabel(citation))} · Chunk ${citation.chunkIndex}${citation.pageNumber ? `, page ${citation.pageNumber}` : ""} · score ${Number(citation.score).toFixed(3)}</span>
            <span class="row-meta">${escapeHtml(citation.snippet)}</span>
        </div>
    `).join("");
}

function citationLabel(citation) {
    if (!citation.isGeneratedArtifact) {
        return "Source text";
    }

    if (citation.artifactKind === "book-club-profile") {
        return "Generated book-club profile";
    }

    if (citation.artifactKind === "name-profile") {
        return "Generated name profile";
    }

    return "Generated retrieval aid";
}

function escapeHtml(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

await refreshDocuments();
setInterval(refreshDocuments, 5000);
