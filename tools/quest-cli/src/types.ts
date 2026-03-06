export interface DeviceInfo {
  serial: string;
  model: string;
  androidVersion: string;
  batteryLevel: number;
  batteryCharging: boolean;
  storageTotal: number;
  storageFree: number;
  appVersion: string | null;
}

export interface SessionInfo {
  name: string;
  size: number;
  dateStr: string;
  location: "device" | "local" | "both";
}

export interface SessionFile {
  name: string;
  size: number;
}

export type LogLevel = "V" | "D" | "I" | "W" | "E" | "F";
