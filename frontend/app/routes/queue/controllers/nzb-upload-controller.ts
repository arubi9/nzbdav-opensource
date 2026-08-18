import { useEffect } from "react";
import type { UploadingFile } from "../route";
import { csrfUpload } from "~/utils/csrf-fetch";

export function initializeUploadController(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    uploadingFiles: UploadingFile[],
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
) {
    useEffect(() => {
        processUploadQueue(isUploadingRef, uploadQueueRef, setUploadingFiles);
    }, [uploadingFiles]);
}

async function processUploadQueue(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void
) {
    if (isUploadingRef.current || uploadQueueRef.current.length === 0) return;

    isUploadingRef.current = true;
    const fileToUpload = uploadQueueRef.current[0];

    setUploadingFiles(files => files.map(f =>
        f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
            ? { ...f, queueSlot: { ...f.queueSlot, status: 'uploading' } }
            : f
    ));

    try {
        const formData = new FormData();
        formData.append('nzbFile', fileToUpload.file, fileToUpload.file.name);

        const xhr = await csrfUpload(
            `/api?mode=addfile&cat=${fileToUpload.queueSlot.cat}&priority=0&pp=0`,
            formData,
            (loaded, total) => {
                const progress = Math.round((loaded / total) * 100);
                setUploadingFiles(files => files.map(f =>
                    f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
                        ? {
                            ...f,
                            queueSlot: {
                                ...f.queueSlot,
                                percentage: progress.toString(),
                                true_percentage: progress.toString()
                            }
                        }
                        : f
                ));
            },
        );
        const response: any = xhr.response;

        if (response.status == false) {
            throw new Error(response.error);
        }

    } catch (error) {
        setUploadingFiles(files => files.map(f =>
            f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id ? {
                ...f,
                queueSlot: {
                    ...f.queueSlot,
                    status: 'upload failed',
                    error: 'Upload failed.'
                }
            } : f
        ));
    }

    uploadQueueRef.current = uploadQueueRef.current.filter(x => x !== fileToUpload);
    isUploadingRef.current = false;

    if (uploadQueueRef.current.length > 0) {
        processUploadQueue(isUploadingRef, uploadQueueRef, setUploadingFiles);
    }
}