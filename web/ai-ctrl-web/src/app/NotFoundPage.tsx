import { Link } from 'react-router-dom'
import { Compass } from 'lucide-react'
import styles from './NotFoundPage.module.css'

export function NotFoundPage() {
  return (
    <div className={styles.page}>
      <Compass size={26} aria-hidden="true" />
      <h1 className={styles.title}>페이지를 찾을 수 없습니다</h1>
      <p className={styles.description}>
        주소가 바뀌었거나 아직 구현되지 않은 화면입니다.
      </p>
      <Link to="/" className={styles.link}>
        콘솔로 돌아가기
      </Link>
    </div>
  )
}
