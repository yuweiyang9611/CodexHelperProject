<script setup lang="ts">
import { computed } from 'vue'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const maxSkillCount = computed(() => Math.max(...props.snapshot.skills.map((skill) => skill.count), 1))
</script>

<template>
  <div class="skill-layout">
    <div class="skill-intro"><span class="skill-orbit" aria-hidden="true">S</span><div><span>工作流归因</span><h3>Skill 使用结构</h3><p>根据本机会话中的显式 Skill 调用聚合；不保存、不展示、不上传工具参数正文。</p></div></div>
    <div v-if="snapshot.skills.length" class="skill-grid">
      <article v-for="(skill, index) in snapshot.skills" :key="skill.id" class="skill-card">
        <span class="skill-number">{{ String(index + 1).padStart(2, '0') }}</span><div class="skill-glyph">{{ skill.name.slice(0, 1).toUpperCase() }}</div><div class="skill-name"><strong>{{ skill.name }}</strong><span>{{ skill.count }} 次加载</span></div><div class="skill-bar"><i :style="{ width: `${skill.count / maxSkillCount * 100}%` }" /></div>
      </article>
    </div>
    <div v-else class="large-empty">暂无可归因的 Skill 使用记录</div>
  </div>
</template>
