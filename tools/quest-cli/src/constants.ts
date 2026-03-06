export const PACKAGE_NAME = "com.samusynth.OpenQuestCapture";
export const DEVICE_FILES_DIR = `/sdcard/Android/data/${PACKAGE_NAME}/files`;
export const SESSION_REGEX = /^\d{8}_\d{6}$/;
export const LOCAL_RECORDINGS_DIR = "recordings";
export const LOCAL_BUILDS_DIR = "Builds";

export const LOGCAT_TAGS = ["RealityLog:*", "Unity:*"];
export const LOGCAT_FILTER = LOGCAT_TAGS.join(" ");
