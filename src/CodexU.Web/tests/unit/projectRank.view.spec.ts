import { createApp, nextTick } from 'vue'
import { expect, it } from 'vitest'
import ProjectRankView from '../../src/views/ProjectRankView.vue'
import { projectUsage, snapshot } from './fixtures'

it('uses the host rankings and distinguishes a priced zero from missing or partial cost', async () => {
  const data = snapshot({
    projects: [
      projectUsage({ id: 'a', name: 'free', tokens: 30, costStatus: 'known' }),
      projectUsage({ id: 'b', name: 'mixed', tokens: 20, costStatus: 'partial' }),
      projectUsage({ id: 'c', name: 'unknown', tokens: 10, costStatus: 'unknown' }),
    ],
    projectRanking: {
      byCost: [{ projectIndex: 1, amount: 3 }, { projectIndex: 0, amount: 0 }, { projectIndex: 2 }],
      byTokens: [{ projectIndex: 0, amount: 0 }, { projectIndex: 1, amount: 3 }, { projectIndex: 2 }],
      hasCost: true, hasEstimatedCost: false,
    },
  })
  const container = document.createElement('div')
  const app = createApp(ProjectRankView, { snapshot: data })
  app.mount(container)
  try {
    const rows = () => [...container.querySelectorAll('.rank-row')]
    expect(rows().map(r => r.querySelector('.rank-info strong')?.textContent)).toEqual(['mixed', 'free', 'unknown'])
    expect(rows().map(r => r.querySelector('.rank-value small')?.textContent)).toEqual(['≥ US$3.00', 'US$0.00', '成本不可得'])
    container.querySelectorAll<HTMLButtonElement>('.rank-sort button')[1]!.click()
    await nextTick()
    expect(rows().map(r => r.querySelector('.rank-info strong')?.textContent)).toEqual(['free', 'mixed', 'unknown'])
  } finally {
    app.unmount()
  }
})
