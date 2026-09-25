import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import DriverPage from './DriverPage'
import './styles.css'

const driverRoute = window.location.pathname.match(/^\/driver\/([0-9a-fA-F-]{36})\/?$/)

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    {driverRoute ? <DriverPage routeId={driverRoute[1]} /> : <App />}
  </React.StrictMode>,
)
