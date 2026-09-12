import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import SurfaceView from './views/SurfaceView.vue'
import './style.css'

createApp(window.codexUSurface ? SurfaceView : App).use(createPinia()).mount('#app')
