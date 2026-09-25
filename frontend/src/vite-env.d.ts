/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_OSRM_BASE_URL?: string
  readonly VITE_ETA_STOP_MINUTES?: string
  readonly VITE_DELIVERY_CUTOFF_TIME?: string
}
